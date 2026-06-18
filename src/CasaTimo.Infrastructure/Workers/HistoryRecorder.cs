using System.Text.Json;
using CasaTimo.Core.Models;
using CasaTimo.Infrastructure.Data;
using CasaTimo.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CasaTimo.Infrastructure.Workers;

/// <summary>
/// Subscribes to all casatimo/# MQTT topics and persists each reading to SQLite.
/// Expected topic: casatimo/{deviceId}/{metric}
/// Expected payload: JSON {"value": 42.5, "unit": "kW"} or plain double string.
/// </summary>
public class HistoryRecorder : BackgroundService
{
    private readonly ILogger<HistoryRecorder> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MqttClientService _mqtt;

    public HistoryRecorder(ILogger<HistoryRecorder> logger, IServiceScopeFactory scopeFactory, MqttClientService mqtt)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _mqtt = mqtt;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _mqtt.MessageReceived += HandleMessageAsync;
        await _mqtt.SubscribeAsync("casatimo/#", stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _mqtt.MessageReceived -= HandleMessageAsync;
        return base.StopAsync(cancellationToken);
    }

    private async Task HandleMessageAsync(string topic, string payload)
    {
        // topic: casatimo/{deviceId}/{metric}
        var parts = topic.Split('/');
        if (parts.Length < 3) return;

        var deviceId = parts[1];
        var metric = parts[2];

        double value = 0;
        string? unit = null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var v)) value = v.GetDouble();
            if (root.TryGetProperty("unit", out var u)) unit = u.GetString();
        }
        catch
        {
            if (!double.TryParse(payload, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                _logger.LogWarning("HistoryRecorder: cannot parse payload for topic {Topic}", topic);
                return;
            }
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CasaTimoDbContext>();
            db.SensorReadings.Add(new SensorReading
            {
                DeviceId = deviceId,
                Metric = metric,
                Value = value,
                Unit = unit,
                Timestamp = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            _logger.LogDebug("HistoryRecorder: saved {DeviceId}/{Metric} = {Value}", deviceId, metric, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HistoryRecorder: failed to save reading for {Topic}", topic);
        }
    }
}
