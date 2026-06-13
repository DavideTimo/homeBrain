using CasaTimo.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;

namespace CasaTimo.Infrastructure.Mqtt;

public class MqttClientService : IMqttService, IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly ILogger<MqttClientService> _logger;
    private readonly List<(string Filter, Func<string, string, Task> Handler)> _handlers = [];

    public bool IsConnected => _client.IsConnected;

    public MqttClientService(IConfiguration config, ILogger<MqttClientService> logger)
    {
        _logger = logger;
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        var host = config["MQTT_HOST"] ?? "localhost";
        var port = int.Parse(config["MQTT_PORT"] ?? "1883");
        var user = config["MQTT_USER"] ?? "";
        var pass = config["MQTT_PASS"] ?? "";

        _options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithCredentials(user, pass)
            .WithClientId($"casatimo-{Environment.MachineName}")
            .WithCleanSession()
            .Build();

        _client.ApplicationMessageReceivedAsync += OnMessageReceived;
        _client.DisconnectedAsync += OnDisconnected;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            await _client.ConnectAsync(_options, ct);
            _logger.LogInformation("Connected to MQTT broker");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to MQTT broker");
            throw;
        }
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken ct = default)
    {
        if (!_client.IsConnected)
            await ConnectAsync(ct);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag()
            .Build();

        await _client.PublishAsync(message, ct);
    }

    public async Task SubscribeAsync(string topicFilter, Func<string, string, Task> handler, CancellationToken ct = default)
    {
        if (!_client.IsConnected)
            await ConnectAsync(ct);

        _handlers.Add((topicFilter, handler));

        await _client.SubscribeAsync(new MqttTopicFilterBuilder()
            .WithTopic(topicFilter)
            .Build(), ct);

        _logger.LogInformation("Subscribed to {Topic}", topicFilter);
    }

    private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.ConvertPayloadToString();

        foreach (var (filter, handler) in _handlers)
        {
            if (MqttTopicFilterComparer.Compare(topic, filter) == MqttTopicFilterCompareResult.IsMatch)
            {
                try { await handler(topic, payload); }
                catch (Exception ex) { _logger.LogError(ex, "Error in MQTT handler for {Topic}", topic); }
            }
        }
    }

    private async Task OnDisconnected(MqttClientDisconnectedEventArgs e)
    {
        _logger.LogWarning("Disconnected from MQTT broker. Reconnecting in 5s...");
        await Task.Delay(TimeSpan.FromSeconds(5));
        try { await _client.ConnectAsync(_options); }
        catch (Exception ex) { _logger.LogError(ex, "Reconnect failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
            await _client.DisconnectAsync();
        _client.Dispose();
    }
}
