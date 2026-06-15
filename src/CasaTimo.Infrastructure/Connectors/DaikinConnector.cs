using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CasaTimo.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CasaTimo.Infrastructure.Connectors;

/// <summary>
/// Daikin Cloud Europe (Unicloud) connector.
/// Autentica tramite Keycloak (password grant), scarica lo stato di tutti i
/// gateway/dispositivi e pubblica su MQTT.
///
/// Topics pubblicati (n = indice zona 0-based):
///   casatimo/daikin/zone/{n}/power              on|off
///   casatimo/daikin/zone/{n}/mode               cooling|heating|auto|fan|dry
///   casatimo/daikin/zone/{n}/temperature/room   °C attuale
///   casatimo/daikin/zone/{n}/temperature/setpoint °C impostata
///   casatimo/daikin/zone/{n}/fan/speed          auto|1|2|3|4|5
///   casatimo/daikin/zone/{n}/fan/direction      horizontal|vertical|3dwind|stop
/// </summary>
public class DaikinConnector : IHostedService, IDisposable
{
    private readonly ILogger<DaikinConnector> _logger;
    private readonly IMessageBroker _broker;
    private readonly DaikinOptions _options;
    private readonly HttpClient _http;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public DaikinConnector(
        IConfiguration configuration,
        ILogger<DaikinConnector> logger,
        IHttpClientFactory httpFactory,
        IMessageBroker broker)
    {
        _logger  = logger;
        _broker  = broker;
        _options = new DaikinOptions();
        configuration.GetSection("Daikin").Bind(_options);
        _http = httpFactory.CreateClient("daikin");
        _http.BaseAddress = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.Username) || string.IsNullOrEmpty(_options.Password))
        {
            _logger.LogInformation("DaikinConnector: Username/Password non configurati, skip");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await EnsureTokenAsync(ct)) goto delay;
                await PollDevicesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "DaikinConnector: errore nel ciclo di polling"); }

            delay:
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct).ConfigureAwait(false);
        }
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    private async Task<bool> EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry) return true;

        try
        {
            _logger.LogInformation("DaikinConnector: richiesta token OAuth2");
            using var tokenClient = new HttpClient();
            var pairs = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "password"),
                new("client_id",  _options.ClientId),
                new("username",   _options.Username!),
                new("password",   _options.Password!)
            };
            var resp = await tokenClient.PostAsync(_options.TokenEndpoint,
                new FormUrlEncodedContent(pairs), ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("DaikinConnector: token request fallita: {status}", resp.StatusCode);
                _accessToken = null;
                return false;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            _accessToken  = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expEl)
                ? expEl.GetInt32() : 300;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 30);

            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            _logger.LogInformation("DaikinConnector: token ottenuto, scade in {s}s", expiresIn);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DaikinConnector: errore ottenimento token");
            _accessToken = null;
            return false;
        }
    }

    // ── Polling ───────────────────────────────────────────────────────────────

    private async Task PollDevicesAsync(CancellationToken ct)
    {
        var resp = await _http.GetAsync("v1/gateway-devices", ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("DaikinConnector: getDevices {status}", resp.StatusCode);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                _accessToken = null; // forza refresh al prossimo ciclo
            return;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc    = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // Risposta: array di gateway device, ognuno con "managementPoints"
        var root = doc.RootElement;
        var devices = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var d) ? d : root;

        int zoneIndex = 0;
        foreach (var device in devices.EnumerateArray())
        {
            await ProcessDeviceAsync(device, ref zoneIndex, ct);
        }
    }

    private async Task ProcessDeviceAsync(JsonElement device, ref int zoneIndex, CancellationToken ct)
    {
        // Ogni gateway ha uno o più "managementPoints" (indoor unit = una zona)
        if (!device.TryGetProperty("managementPoints", out var mgmtPoints)) return;

        foreach (var point in mgmtPoints.EnumerateObject())
        {
            var pointData = point.Value;
            // Skippa il management point "gateway" — ci interessano solo le unità indoor
            if (pointData.TryGetProperty("managementPointType", out var typeEl) &&
                typeEl.GetString() is "gateway") continue;

            var zone = zoneIndex++;
            var prefix = $"casatimo/daikin/zone/{zone}";

            await PublishDataPointAsync(pointData, "onOffMode",
                $"{prefix}/power", ct, v => v);

            await PublishDataPointAsync(pointData, "operationMode",
                $"{prefix}/mode", ct, v => v);

            await PublishDataPointAsync(pointData, "fanSpeed",
                $"{prefix}/fan/speed", ct, v => v);

            await PublishDataPointAsync(pointData, "fanDirection",
                $"{prefix}/fan/direction", ct, v => v);

            // Temperature: dentro "temperatureControl" → "operationModes" → modalità attiva
            await PublishTemperaturesAsync(pointData, prefix, ct);
        }
    }

    /// <summary>
    /// Pubblica temperatura stanza e setpoint dalla struttura Daikin Cloud.
    /// Percorso: temperatureControl.value.operationModes.{mode}.setpoints.roomTemperature
    /// </summary>
    private async Task PublishTemperaturesAsync(
        JsonElement point, string prefix, CancellationToken ct)
    {
        if (!point.TryGetProperty("temperatureControl", out var tempControl)) return;
        if (!tempControl.TryGetProperty("value", out var tcValue)) return;
        if (!tcValue.TryGetProperty("operationModes", out var opModes)) return;

        // Leggi sensore temperatura stanza se presente direttamente
        if (point.TryGetProperty("roomTemperature", out var roomTempEl) &&
            roomTempEl.TryGetProperty("value", out var roomVal) &&
            roomVal.ValueKind == JsonValueKind.Number)
        {
            await _broker.PublishAsync($"{prefix}/temperature/room",
                roomVal.GetDouble().ToString("G4", CultureInfo.InvariantCulture), ct);
        }

        // Setpoint dalla modalità attiva
        foreach (var mode in opModes.EnumerateObject())
        {
            var modeData = mode.Value;
            if (!modeData.TryGetProperty("setpoints", out var setpoints)) continue;
            if (!setpoints.TryGetProperty("roomTemperature", out var setpointEl)) continue;
            if (!setpointEl.TryGetProperty("value", out var setValue) ||
                setValue.ValueKind != JsonValueKind.Number) continue;

            await _broker.PublishAsync($"{prefix}/temperature/setpoint",
                setValue.GetDouble().ToString("G4", CultureInfo.InvariantCulture), ct);

            _logger.LogInformation("DaikinConnector: {prefix}/temperature/setpoint = {v}",
                prefix, setValue.GetDouble());
            break; // prendi solo la prima modalità con un setpoint
        }
    }

    private async Task PublishDataPointAsync(
        JsonElement point,
        string dataPointName,
        string topic,
        CancellationToken ct,
        Func<string, string> transform)
    {
        if (!point.TryGetProperty(dataPointName, out var dp)) return;
        if (!dp.TryGetProperty("value", out var valueEl)) return;

        var raw = valueEl.ValueKind == JsonValueKind.String
            ? valueEl.GetString() ?? string.Empty
            : valueEl.ValueKind == JsonValueKind.Number
                ? valueEl.GetDouble().ToString("G4", CultureInfo.InvariantCulture)
                : valueEl.ToString();

        var payload = transform(raw);
        await _broker.PublishAsync(topic, payload, ct);
        _logger.LogInformation("DaikinConnector: {topic} = {value}", topic, payload);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loopTask != null)
        {
            try { await _loopTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose() => _cts?.Dispose();
}
