using Microsoft.AspNetCore.SignalR.Client;

namespace CasaTimo.Web.Services;

public class SensorService : IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly IConfiguration _config;

    public SensorService(IConfiguration config) => _config = config;

    public async Task ConnectAsync(Action<string, string> onUpdate)
    {
        var apiBase = _config["ApiBase"] ?? "http://localhost:5000";
        _connection = new HubConnectionBuilder()
            .WithUrl($"{apiBase}/hubs/sensors")
            .WithAutomaticReconnect()
            .Build();

        _connection.On<SensorUpdate>("LiveUpdate", update =>
        {
            if (update?.Topic != null && update.Payload != null)
                onUpdate(update.Topic, update.Payload);
        });

        await _connection.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
            await _connection.DisposeAsync();
    }

    private record SensorUpdate(string Topic, string Payload, DateTime Timestamp);
}
