using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;

namespace CasaTimo.Infrastructure.Messaging;

public class MqttClientService : IHostedService, IMessageBroker, IDisposable
{
    private readonly ILogger<MqttClientService> _logger;
    private readonly MqttOptions _options;
    private IMqttClient? _client;

    public event Func<string, string, Task>? MessageReceived;

    public MqttClientService(IConfiguration configuration, ILogger<MqttClientService> logger)
    {
        _logger = logger;
        _options = new MqttOptions();
        configuration.GetSection("Mqtt").Bind(_options);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            _client.ApplicationMessageReceivedAsync += async e =>
            {
                var topic = e.ApplicationMessage.Topic;
                var payload = e.ApplicationMessage.Payload == null
                    ? string.Empty
                    : Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                _logger.LogDebug("MQTT received: {topic} -> {payload}", topic, payload);

                if (MessageReceived != null)
                {
                    try { await MessageReceived(topic, payload); }
                    catch (Exception ex) { _logger.LogError(ex, "Error in MessageReceived handler for topic {topic}", topic); }
                }
            };

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId(_options.ClientId ?? Guid.NewGuid().ToString())
                .WithTcpServer(_options.Host ?? "localhost", _options.Port);

            if (!string.IsNullOrEmpty(_options.Username))
                optionsBuilder = optionsBuilder.WithCredentials(_options.Username, _options.Password);

            if (_options.UseTls)
                optionsBuilder = optionsBuilder.WithTlsOptions(o => { });

            await _client.ConnectAsync(optionsBuilder.Build(), cancellationToken);
            _logger.LogInformation("MQTT connected to {host}:{port}", _options.Host, _options.Port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start MQTT client");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client?.IsConnected == true)
        {
            await _client.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken);
            _logger.LogInformation("MQTT client disconnected");
        }
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("MQTT client is not connected");

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }

    public async Task SubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("MQTT client is not connected");

        await _client.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(topic).Build(), cancellationToken);
        _logger.LogInformation("Subscribed to MQTT topic {topic}", topic);
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}
