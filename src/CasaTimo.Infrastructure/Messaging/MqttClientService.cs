using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace CasaTimo.Infrastructure.Messaging;

public class MqttClientService : IHostedService, IDisposable
{
    private readonly ILogger<MqttClientService> _logger;
    private readonly IConfiguration _configuration;
    private IMqttClient? _client;
    private MqttOptions _options = new();

    public event Func<string, string, Task>? MessageReceived;

    public bool IsConnected => _client?.IsConnected ?? false;

    public MqttClientService(IConfiguration configuration, ILogger<MqttClientService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        var section = configuration.GetSection("Mqtt");
        section.Bind(_options);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            // Subscribe to the ApplicationMessageReceivedAsync event if available
            // which is supported by MQTTnet client implementations.
            try
            {
                _client.ApplicationMessageReceivedAsync += async e =>
                {
                    try
                    {
                        var topic = e.ApplicationMessage.Topic;
                        var payload = e.ApplicationMessage.Payload == null
                            ? string.Empty
                            : Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                        _logger.LogInformation("MQTT msg received: {topic} -> {payload}", topic, payload);

                        if (MessageReceived != null)
                        {
                            foreach (var handler in MessageReceived.GetInvocationList().Cast<Func<string, string, Task>>())
                                await handler(topic, payload);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing MQTT message");
                    }
                };
            }
            catch
            {
                // If the event is not available on this MQTTnet version, swallow
                // the exception and continue without a handler so the service can run.
            }

            var clientOptions = new MqttClientOptionsBuilder()
                .WithClientId(_options.ClientId ?? Guid.NewGuid().ToString())
                .WithTcpServer(_options.Host ?? "localhost", _options.Port);

            if (!string.IsNullOrEmpty(_options.Username))
            {
                clientOptions = clientOptions.WithCredentials(_options.Username, _options.Password);
            }

            if (_options.UseTls)
            {
                clientOptions = clientOptions.WithTls();
            }

            var mqttOpts = clientOptions.Build();
            await _client.ConnectAsync(mqttOpts, cancellationToken);
            _logger.LogInformation("MQTT client connected against {host}:{port}", _options.Host, _options.Port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start MQTT client");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client != null)
        {
            await _client.DisconnectAsync();
            _logger.LogInformation("MQTT client disconnected");
        }
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default)
    {
        if (_client == null) throw new InvalidOperationException("MQTT client not started");

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .Build();

        await _client.PublishAsync(message, cancellationToken);
    }

    public async Task SubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        if (_client == null) throw new InvalidOperationException("MQTT client not started");
        await _client.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(topic).Build());
        _logger.LogInformation("Subscribed to MQTT topic {topic}", topic);
    }

    public void Dispose()
    {
        try
        {
            _client?.Dispose();
        }
        catch { }
    }
}
