using System.Text.Json;
using CasaTimo.Camera.Pipeline;
using CasaTimo.Infrastructure.Data;
using CasaTimo.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace CasaTimo.Camera;

/// <summary>
/// Un loop motion→YOLO→tracking per camera abilitata (vedi /api/cameras), tutte
/// in parallelo nello stesso processo. Pubblica solo eventi via MQTT (mai i
/// frame), coerente con la nota architetturale in README: la telecamera non è
/// un sensore "condiviso via software" come gli altri, ogni consumer (questa
/// pipeline, la live view HLS) apre una propria connessione RTSP diretta.
///
/// Non verificato: scritto senza possibilità di compilare/eseguire in questo
/// ambiente (dotnet non disponibile). Va provato per primo contro una camera
/// reale con un modello scaricato in models/yolov8n.onnx.
/// </summary>
public class CameraSurveillanceWorker : BackgroundService
{
    private readonly ILogger<CameraSurveillanceWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MqttClientService _mqtt;
    private readonly CameraOptions _options;

    public CameraSurveillanceWorker(
        ILogger<CameraSurveillanceWorker> logger,
        IServiceScopeFactory scopeFactory,
        MqttClientService mqtt,
        IOptions<CameraOptions> options)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _mqtt = mqtt;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var i = 0; i < 30 && !stoppingToken.IsCancellationRequested; i++)
        {
            if (_mqtt.IsConnected) break;
            await Task.Delay(1000, stoppingToken);
        }
        if (!_mqtt.IsConnected)
        {
            _logger.LogWarning("CameraSurveillanceWorker: MQTT non connesso dopo 30s, servizio non avviato.");
            return;
        }

        List<CasaTimo.Core.Models.Camera> cameras;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CasaTimoDbContext>();
            cameras = await db.Cameras.Where(c => c.Enabled).ToListAsync(stoppingToken);
        }

        if (cameras.Count == 0)
        {
            _logger.LogInformation(
                "CameraSurveillanceWorker: nessuna camera abilitata (configurale da /telecamere). " +
                "Riavvia il servizio dopo averne aggiunta una — il caricamento non è dinamico in questa versione.");
            return;
        }

        if (!File.Exists(_options.ModelPath))
        {
            _logger.LogError(
                "CameraSurveillanceWorker: modello YOLO non trovato in {Path} — pipeline non avviata. " +
                "Scaricalo/esportalo (vedi README) e configura Camera:ModelPath.", _options.ModelPath);
            return;
        }

        // Un solo YoloDetector condiviso tra tutte le camere: ONNX Runtime supporta
        // Run() concorrenti sulla stessa InferenceSession, evita di caricare N volte
        // lo stesso modello in RAM per N camere.
        using var yolo = new YoloDetector(_options.ModelPath, _options.ConfThreshold, _options.IouThreshold);

        var retentionTask = RunRetentionCleanupLoopAsync(cameras, stoppingToken);
        var cameraTasks = cameras.Select(cam => RunCameraLoopAsync(cam, yolo, stoppingToken));

        await Task.WhenAll(cameraTasks.Append(retentionTask));
    }

    private async Task RunCameraLoopAsync(CasaTimo.Core.Models.Camera camera, YoloDetector yolo, CancellationToken ct)
    {
        var rtspUrl = BuildRtspUrl(camera);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessStreamAsync(camera, rtspUrl, yolo, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Camera {Name}: errore nella pipeline, riconnessione tra 10s", camera.Name);
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private async Task ProcessStreamAsync(
        CasaTimo.Core.Models.Camera camera, string rtspUrl, YoloDetector yolo, CancellationToken ct)
    {
        using var capture = new VideoCapture(rtspUrl);
        if (!capture.IsOpened())
        {
            _logger.LogWarning("Camera {Name}: impossibile aprire {Url}", camera.Name, MaskCredentials(rtspUrl));
            return;
        }
        _logger.LogInformation("Camera {Name}: connessa ({Width}x{Height})",
            camera.Name, capture.FrameWidth, capture.FrameHeight);

        using var motionDetector = new MotionDetector(minBlobArea: _options.MinMotionArea);
        var trackManager = new TrackManager(missLimit: _options.TrackMissLimit);
        using var frame = new Mat();

        var frameIndex = 0;
        var frameWidth = capture.FrameWidth;
        var frameHeight = capture.FrameHeight;
        var lastSnapshotAt = DateTime.MinValue;
        var lastStatusAt = DateTime.MinValue;
        var fpsWindowStart = DateTime.UtcNow;
        var fpsWindowFrames = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!capture.Read(frame) || frame.Empty())
            {
                _logger.LogWarning("Camera {Name}: stream interrotto, riconnessione", camera.Name);
                return;
            }
            frameIndex++;
            fpsWindowFrames++;

            var hadTracksBefore = trackManager.Tracks.Count > 0;

            var lostIds = trackManager.UpdateAll(frame);
            foreach (var id in lostIds)
                _logger.LogInformation("Camera {Name}: track #{Id} perso", camera.Name, id);

            if (_options.ReverifyEveryFrames > 0 && frameIndex % _options.ReverifyEveryFrames == 0)
            {
                foreach (var track in trackManager.Tracks.ToList())
                {
                    var region = InflateAndClamp(track.BBoxAsRect(), frameWidth, frameHeight, padding: 20);
                    if (region.Width <= 0 || region.Height <= 0) continue;

                    using var reverifyCrop = new Mat(frame, region);
                    var reverifyDetections = yolo.Detect(reverifyCrop);
                    var best = reverifyDetections.OrderByDescending(d => d.Confidence).FirstOrDefault();
                    trackManager.TryReconcile(track, best);
                }
            }

            var motionBoxes = motionDetector.Detect(frame);
            foreach (var box in motionBoxes)
            {
                if (trackManager.IsCoveredByExistingTrack(box)) continue;

                using var crop = new Mat(frame, box);
                var detections = yolo.Detect(crop);
                foreach (var d in detections)
                {
                    var absoluteBox = new Rect(box.X + d.Box.X, box.Y + d.Box.Y, d.Box.Width, d.Box.Height);
                    var track = trackManager.StartTrack(frame, new Detection(d.Label, d.Confidence, absoluteBox));
                    _logger.LogInformation("Camera {Name}: nuovo track #{Id} {Label} ({Conf:P0})",
                        camera.Name, track.Id, d.Label, d.Confidence);
                    await PublishEventAsync(camera, EventTypeForLabel(d.Label), d.Confidence, count: 1, ct);
                }
            }

            var hasTracksNow = trackManager.Tracks.Count > 0;
            if (!hadTracksBefore && hasTracksNow)
                await PublishEventAsync(camera, "motion", confidence: null, count: 1, ct); // motion iniziato
            else if (hadTracksBefore && !hasTracksNow)
                await PublishEventAsync(camera, "motion", confidence: null, count: 0, ct); // motion terminato

            if (hasTracksNow && (DateTime.UtcNow - lastSnapshotAt).TotalSeconds >= _options.SnapshotIntervalSeconds)
            {
                lastSnapshotAt = DateTime.UtcNow;
                SaveSnapshot(camera, frame);
            }

            if ((DateTime.UtcNow - lastStatusAt).TotalSeconds >= _options.StatusPublishIntervalSeconds)
            {
                lastStatusAt = DateTime.UtcNow;
                var elapsedFps = fpsWindowFrames / Math.Max((DateTime.UtcNow - fpsWindowStart).TotalSeconds, 0.001);
                fpsWindowFrames = 0;
                fpsWindowStart = DateTime.UtcNow;

                await PublishNumericAsync(camera, "online", 1, null, ct);
                await PublishNumericAsync(camera, "fps", elapsedFps, "fps", ct);
            }
        }
    }

    private static string EventTypeForLabel(string label) => label switch
    {
        "person" => "person",
        "car" or "truck" or "bus" or "motorcycle" or "bicycle" => "vehicle",
        "dog" or "cat" or "bird" => "animal",
        _ => "person"
    };

    private void SaveSnapshot(CasaTimo.Core.Models.Camera camera, Mat frame)
    {
        try
        {
            var dayFolder = Path.Combine(_options.NasBasePath, camera.Id, DateTime.UtcNow.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dayFolder);
            var fileName = DateTime.UtcNow.ToString("HHmmss_fff") + ".jpg";
            Cv2.ImWrite(Path.Combine(dayFolder, fileName), frame);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Camera {Name}: impossibile salvare lo snapshot su NAS", camera.Name);
        }
    }

    private async Task RunRetentionCleanupLoopAsync(List<CasaTimo.Core.Models.Camera> cameras, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var camera in cameras)
            {
                try
                {
                    var cameraFolder = Path.Combine(_options.NasBasePath, camera.Id);
                    if (!Directory.Exists(cameraFolder)) continue;

                    var cutoff = DateTime.UtcNow.Date.AddDays(-_options.RetentionDays);
                    foreach (var dayDir in Directory.GetDirectories(cameraFolder))
                    {
                        var name = Path.GetFileName(dayDir);
                        if (DateTime.TryParseExact(name, "yyyy-MM-dd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var day) && day < cutoff)
                        {
                            Directory.Delete(dayDir, recursive: true);
                            _logger.LogInformation("Camera {Name}: rimossa cartella oltre retention {Day}", camera.Name, name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Camera {Name}: errore nella pulizia retention", camera.Name);
                }
            }

            try { await Task.Delay(TimeSpan.FromDays(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PublishEventAsync(
        CasaTimo.Core.Models.Camera camera, string eventType, double? confidence, int? count, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { confidence, count, timestamp = DateTime.UtcNow });
        await _mqtt.PublishAsync($"casatimo/camera_{camera.Id}/{eventType}", payload, ct);
    }

    private async Task PublishNumericAsync(
        CasaTimo.Core.Models.Camera camera, string metric, double value, string? unit, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { value, unit });
        await _mqtt.PublishAsync($"casatimo/camera_{camera.Id}/{metric}", payload, ct);
    }

    private static string BuildRtspUrl(CasaTimo.Core.Models.Camera camera)
    {
        var userInfo = string.IsNullOrEmpty(camera.Username)
            ? ""
            : $"{Uri.EscapeDataString(camera.Username)}:{Uri.EscapeDataString(camera.Password ?? "")}@";
        // Preferisce il sub-stream a bassa risoluzione per la pipeline AI se configurato
        // (vedi README STEP 13), altrimenti usa il path main.
        var path = string.IsNullOrWhiteSpace(camera.RtspSubPath) ? camera.RtspPath : camera.RtspSubPath;
        if (!path.StartsWith('/')) path = "/" + path;
        return $"rtsp://{userInfo}{camera.Host}:{camera.RtspPort}{path}";
    }

    private static string MaskCredentials(string rtspUrl) =>
        System.Text.RegularExpressions.Regex.Replace(rtspUrl, @"//[^/@]+@", "//***:***@");

    private static Rect InflateAndClamp(Rect r, int frameWidth, int frameHeight, int padding)
    {
        var x = Math.Max(0, r.X - padding);
        var y = Math.Max(0, r.Y - padding);
        var right = Math.Min(frameWidth, r.X + r.Width + padding);
        var bottom = Math.Min(frameHeight, r.Y + r.Height + padding);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}
