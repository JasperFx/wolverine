using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Postgresql.Internal;

public sealed class PostgresTopic : PostgresEndpoint
{
    public PostgresTopic(
        PostgresTransport transport,
        QueueDefinition definition)
        : base(transport, EndpointRole.Application, definition.Uri, definition)
    {
        if (transport == null)
        {
            throw new ArgumentNullException(nameof(transport));
        }

        EndpointName = definition.Name;
    }

    public string TopicName { get; }

    public override ValueTask<IListener> BuildListenerAsync(
        IWolverineRuntime runtime,
        IReceiver receiver)
    {
        throw new NotSupportedException();
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
