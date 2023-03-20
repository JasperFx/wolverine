using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Postgresql.Internal;

public sealed class PostgresQueueSubscription : PostgresEndpoint
{
    public PostgresQueueSubscription(
        PostgresTransport transport,
        PostgresTopic topic,
        QueueDefinition definition)
        : base(transport, EndpointRole.Application, definition.Uri, definition)
    {
        if (transport == null)
        {
            throw new ArgumentNullException(nameof(transport));
        }

        SubscriptionName = EndpointName = definition.Name;
        Topic = topic ?? throw new ArgumentNullException(nameof(topic));
    }

    public string SubscriptionName { get; }

    public PostgresTopic Topic { get; }

    public override ValueTask<IListener> BuildListenerAsync(
        IWolverineRuntime runtime,
        IReceiver receiver)
    {
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }

    public override ValueTask<bool> CheckAsync()
    {
        throw new NotImplementedException();
    }

    public override ValueTask TeardownAsync(ILogger logger)
    {
        throw new NotImplementedException();
    }

    public override ValueTask SetupAsync(ILogger logger)
    {
        throw new NotImplementedException();
    }
}
