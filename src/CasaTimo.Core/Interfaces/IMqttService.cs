namespace CasaTimo.Core.Interfaces;

public interface IMqttService
{
    Task PublishAsync(string topic, string payload, CancellationToken ct = default);
    Task SubscribeAsync(string topicFilter, Func<string, string, Task> handler, CancellationToken ct = default);
    bool IsConnected { get; }
}
