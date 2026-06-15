namespace CasaTimo.Infrastructure.Messaging;

public interface IMessageBroker
{
    Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default);
    Task SubscribeAsync(string topic, CancellationToken cancellationToken = default);
    event Func<string, string, Task> MessageReceived;
}
