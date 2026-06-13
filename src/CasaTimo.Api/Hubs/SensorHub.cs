using Microsoft.AspNetCore.SignalR;

namespace CasaTimo.Api.Hubs;

public class SensorHub : Hub
{
    public async Task SubscribeToDevice(string deviceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"device-{deviceId}");
    }

    public async Task UnsubscribeFromDevice(string deviceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"device-{deviceId}");
    }
}

public class SensorBroadcastService : BackgroundService
{
    private readonly IHubContext<SensorHub> _hub;
    private readonly CasaTimo.Core.Interfaces.IMqttService _mqtt;
    private readonly ILogger<SensorBroadcastService> _logger;

    public SensorBroadcastService(
        IHubContext<SensorHub> hub,
        CasaTimo.Core.Interfaces.IMqttService mqtt,
        ILogger<SensorBroadcastService> logger)
    {
        _hub = hub;
        _mqtt = mqtt;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _mqtt.SubscribeAsync(CasaTimo.Core.Models.MqttTopics.All, async (topic, payload) =>
        {
            var parts = topic.Split('/');
            var deviceId = parts.Length > 1 ? parts[1] : "unknown";

            await _hub.Clients.Group($"device-{deviceId}").SendAsync("SensorUpdate", new
            {
                topic,
                payload,
                timestamp = DateTime.UtcNow
            }, ct);

            await _hub.Clients.All.SendAsync("LiveUpdate", new
            {
                topic,
                payload,
                timestamp = DateTime.UtcNow
            }, ct);
        }, ct);

        await Task.Delay(Timeout.Infinite, ct);
    }
}
