using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Postgresql.Internal;

public sealed class PostgresQueue : PostgresEndpoint, IBrokerQueue
{
    private readonly QueueDefinition _definition;
    private bool _hasInitialized;

    public PostgresQueue(
        PostgresTransport transport,
        QueueDefinition definition,
        EndpointRole role = EndpointRole.Application)
        : base(transport, role, definition.Uri, definition)
    {
        if (transport == null)
        {
            throw new ArgumentNullException(nameof(transport));
        }

        _definition = definition;
        EndpointName = definition.Name;
    }

    public string QueueName => _definition.QueueName;

    public string ChannelName => _definition.ChannelName;

    public async ValueTask PurgeAsync(ILogger logger)
    {
        try
        {
            var receiver = CreateListener();

            var cts = new CancellationTokenSource();
            cts.CancelAfter(5.Seconds());
            while (!cts.IsCancellationRequested)
            {
                var message = await receiver.ReadNext(cts.Token);
                if (message is { Id: var id })
                {
                    await receiver.CompleteAsync(id, cts.Token);
                }
            }
        }
        catch(OperationCanceledException){}
        catch (Exception e)
        {
            logger.LogError(e, "Error trying to purge Azure Service Bus queue {Queue}", QueueName);
        }
    }

    public ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        return ValueTask.FromResult(new Dictionary<string, string>
        {
            { "Name", QueueName }
        });
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_hasInitialized)
        {
            return;
        }

        if (Transport.AutoProvision)
        {
            await SetupAsync(logger);
        }

        if (Transport.AutoPurgeAllQueues)
        {
            await PurgeAsync(logger);
        }

        _hasInitialized = true;
    }

    public override ValueTask<IListener> BuildListenerAsync(
        IWolverineRuntime runtime,
        IReceiver receiver)
    {
        var messageReceiver = CreateListener();
        var mapper = BuildMapper(runtime);
        var listener =
            new PostgresListener(this, runtime.Logger, receiver, messageReceiver, mapper);

        return ValueTask.FromResult<IListener>(listener);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        var mapper = BuildMapper(runtime);
        var sender = CreateSender();
        var protocol = new PostgresSenderProtocol(runtime, this, mapper, sender);

        return new BatchedSender(
            Uri,
            protocol,
            runtime.DurabilitySettings.Cancellation,
            runtime.Logger);
    }

    private PostgresQueueSender CreateSender()
    {
        return new PostgresQueueSender(this);
    }

    private PostgresQueueListener CreateListener()
    {
        return new PostgresQueueListener(this);
    }
}
