using CasaTimo.Core.Interfaces;
using CasaTimo.Core.Models;
using CasaTimo.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CasaTimo.Workers;

public class HistoryRecorder : BackgroundService
{
    private readonly IMqttService _mqtt;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HistoryRecorder> _logger;
    private readonly Dictionary<string, (double Value, DateTime LastUpdate)> _lastValues = [];
    private readonly object _lock = new();

    public HistoryRecorder(IMqttService mqtt, IServiceScopeFactory scopeFactory, ILogger<HistoryRecorder> logger)
    {
        _mqtt = mqtt;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _mqtt.SubscribeAsync(MqttTopics.All, OnMessageReceived, ct);

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var nextHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc)
                .AddHours(1);
            var delay = nextHour - now;

            await Task.Delay(delay, ct);
            await SaveHourlySnapshotAsync(ct);
        }
    }

    private Task OnMessageReceived(string topic, string payload)
    {
        if (!double.TryParse(payload, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value))
            return Task.CompletedTask;

        lock (_lock)
        {
            _lastValues[topic] = (value, DateTime.UtcNow);
        }

        return Task.CompletedTask;
    }

    private async Task SaveHourlySnapshotAsync(CancellationToken ct)
    {
        Dictionary<string, (double Value, DateTime LastUpdate)> snapshot;
        lock (_lock)
        {
            snapshot = new Dictionary<string, (double, DateTime)>(_lastValues);
        }

        if (snapshot.Count == 0) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CasaTimoDbContext>();

        var readings = snapshot.Select(kv => new SensorReading
        {
            MqttTopic = kv.Key,
            DeviceId = ExtractDeviceId(kv.Key),
            Metric = ExtractMetric(kv.Key),
            Value = kv.Value.Value,
            Unit = GuessUnit(kv.Key),
            Timestamp = DateTime.UtcNow
        }).ToList();

        db.SensorReadings.AddRange(readings);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("HistoryRecorder: saved {Count} readings at {Time}", readings.Count, DateTime.UtcNow);
    }

    private static string ExtractDeviceId(string topic)
    {
        var parts = topic.Split('/');
        return parts.Length > 1 ? parts[1] : "unknown";
    }

    private static string ExtractMetric(string topic)
    {
        var parts = topic.Split('/');
        return parts.Length > 2 ? string.Join("/", parts.Skip(2)) : topic;
    }

    private static string GuessUnit(string topic) => topic switch
    {
        var t when t.Contains("temperature") => "°C",
        var t when t.Contains("power") => "kW",
        var t when t.Contains("energy") => "kWh",
        var t when t.Contains("soc") => "%",
        _ => ""
    };
}
