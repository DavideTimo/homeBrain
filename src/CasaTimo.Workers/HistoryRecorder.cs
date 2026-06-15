using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CasaTimo.Core.Models;
using CasaTimo.Infrastructure.Data;
using CasaTimo.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CasaTimo.Workers;

public class HistoryRecorder : BackgroundService
{
    private readonly IMessageBroker _messageBroker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HistoryRecorder> _logger;
    private readonly int _intervalMinutes;

    // Keyed by topic, stores the last received value and unit
    private readonly Dictionary<string, (double Value, string? Unit)> _latestValues = new();
    private readonly object _lock = new();

    public HistoryRecorder(
        IMessageBroker messageBroker,
        IServiceScopeFactory scopeFactory,
        ILogger<HistoryRecorder> logger,
        IConfiguration configuration)
    {
        _messageBroker = messageBroker;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _intervalMinutes = int.TryParse(configuration["HistoryRecorder:IntervalMinutes"], out var minutes)
            ? minutes
            : 60;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _messageBroker.MessageReceived += OnMessageReceived;

        try
        {
            await _messageBroker.SubscribeAsync("casatimo/#", stoppingToken);
            _logger.LogInformation("HistoryRecorder subscribed to casatimo/#. Recording interval: {IntervalMinutes} minutes.", _intervalMinutes);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(_intervalMinutes), stoppingToken);
                await FlushToDatabase(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            _messageBroker.MessageReceived -= OnMessageReceived;
        }
    }

    private Task OnMessageReceived(string topic, string payload)
    {
        // Topic format: casatimo/{deviceId}/{metric}
        // We only handle topics that start with casatimo/
        if (!topic.StartsWith("casatimo/", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        if (!double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            _logger.LogDebug("HistoryRecorder: could not parse double from payload '{Payload}' on topic '{Topic}'. Skipping.", payload, topic);
            return Task.CompletedTask;
        }

        lock (_lock)
        {
            _latestValues[topic] = (value, null);
        }

        return Task.CompletedTask;
    }

    private async Task FlushToDatabase(CancellationToken cancellationToken)
    {
        Dictionary<string, (double Value, string? Unit)> snapshot;

        lock (_lock)
        {
            snapshot = new Dictionary<string, (double Value, string? Unit)>(_latestValues);
        }

        if (snapshot.Count == 0)
        {
            _logger.LogDebug("HistoryRecorder: no values to flush.");
            return;
        }

        _logger.LogInformation("HistoryRecorder: flushing {Count} sensor readings to database.", snapshot.Count);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CasaTimoDbContext>();
        var now = DateTime.UtcNow;

        foreach (var (topic, (value, unit)) in snapshot)
        {
            // Parse topic: casatimo/{deviceId}/{metric}
            // Strip leading "casatimo/" prefix then split on first '/' to get deviceId, rest is metric
            var withoutPrefix = topic.Substring("casatimo/".Length); // e.g. "pdc/temperature/supply"
            var firstSlash = withoutPrefix.IndexOf('/');

            if (firstSlash < 0)
            {
                _logger.LogWarning("HistoryRecorder: topic '{Topic}' has no metric segment after deviceId. Skipping.", topic);
                continue;
            }

            var deviceId = withoutPrefix.Substring(0, firstSlash);
            var metric = withoutPrefix.Substring(firstSlash + 1); // e.g. "temperature/supply"

            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(metric))
            {
                _logger.LogWarning("HistoryRecorder: could not parse deviceId/metric from topic '{Topic}'. Skipping.", topic);
                continue;
            }

            // Ensure device exists
            var device = await db.Devices.FindAsync(new object[] { deviceId }, cancellationToken);
            if (device == null)
            {
                device = new Device
                {
                    Id = deviceId,
                    Name = deviceId,
                    Type = null,
                    IsActive = true
                };
                db.Devices.Add(device);
                _logger.LogInformation("HistoryRecorder: auto-created device '{DeviceId}'.", deviceId);

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "HistoryRecorder: failed to create device '{DeviceId}'.", deviceId);
                    continue;
                }
            }

            var reading = new SensorReading
            {
                DeviceId = deviceId,
                Metric = metric,
                Value = value,
                Unit = unit,
                Timestamp = now
            };

            db.SensorReadings.Add(reading);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("HistoryRecorder: successfully saved sensor readings.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HistoryRecorder: failed to save sensor readings.");
        }
    }
}
