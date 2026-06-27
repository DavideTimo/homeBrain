using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Server;

namespace CasaTimo.Infrastructure.Messaging;

public class MqttBrokerService : IHostedService, IDisposable
{
    private readonly ILogger<MqttBrokerService> _logger;
    private MqttServer? _server;

    public MqttBrokerService(ILogger<MqttBrokerService> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(1883)
            .Build();

        _server = new MqttFactory().CreateMqttServer(options);
        await _server.StartAsync();
        _logger.LogInformation("MQTT broker embedded avviato su porta 1883");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server != null)
        {
            await _server.StopAsync();
            _logger.LogInformation("MQTT broker embedded fermato");
        }
    }

    public void Dispose()
    {
        _server?.Dispose();
    }
}
