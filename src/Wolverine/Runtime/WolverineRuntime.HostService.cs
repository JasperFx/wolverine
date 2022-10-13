using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Lamar;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime;

internal partial class WolverineRuntime
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Build up the message handlers
            await Handlers.CompileAsync(Options, _container);

            if (Options.AutoBuildEnvelopeStorageOnStartup && Persistence is not NullEnvelopePersistence)
            {
                await Persistence.Admin.MigrateAsync();
            }
            
            await startMessagingTransportsAsync();

            startInMemoryScheduledJobs();

            await startDurabilityAgentAsync();
        }
        catch (Exception? e)
        {
            MessageLogger.LogException(e, message: "Failed to start the Wolverine messaging");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_hasStopped)
        {
            return;
        }

        _hasStopped = true;

        // This is important!
        _container.As<Container>().DisposalLock = DisposalLock.Unlocked;

        if (Durability != null)
        {
            await Durability.StopAsync(cancellationToken);
        }


        await _endpoints.DrainAsync();
 
        Advanced.Cancel();
    }

    private void startInMemoryScheduledJobs()
    {
        ScheduledJobs =
            new InMemoryScheduledJobProcessor((ILocalQueue)Endpoints.AgentForLocalQueue(TransportConstants.Replies));

        // Bit of a hack, but it's necessary. Came up in compliance tests
        if (Persistence is NullEnvelopePersistence p)
        {
            p.ScheduledJobs = ScheduledJobs;
        }
    }

    private async Task startMessagingTransportsAsync()
    {
        foreach (var transport in Options.Transports)
        {
            await transport.InitializeAsync(this).ConfigureAwait(false);
            foreach (var endpoint in transport.Endpoints())
            {
                endpoint.Runtime = this; // necessary to locate serialization
                endpoint.Compile(this);
            }
        }

        // Let any registered routing conventions discover listener endpoints
        foreach (var routingConvention in Options.RoutingConventions)
        {
            routingConvention.DiscoverListeners(this, Handlers.Chains.Select(x => x.MessageType).ToList());
        }

        foreach (var transport in Options.Transports)
        {
            var replyUri = transport.ReplyEndpoint()?.Uri;

            foreach (var endpoint in transport.Endpoints().Where(x => x.AutoStartSendingAgent())) endpoint.StartSending(this, replyUri);
        }

        await Endpoints.StartListenersAsync();
    }

    private async Task startDurabilityAgentAsync()
    {
        // HOKEY, BUT IT WORKS
        if (_container.Model.DefaultTypeFor<IEnvelopePersistence>() != typeof(NullEnvelopePersistence) &&
            Options.Advanced.DurabilityAgentEnabled)
        {
            var durabilityLogger = _container.GetInstance<ILogger<DurabilityAgent>>();

            // TODO -- use the worker queue for Retries?
            var worker = new DurableReceiver(new LocalQueueSettings("scheduled"), this, Pipeline);

            Durability = new DurabilityAgent(this, Logger, durabilityLogger, worker, Persistence,
                Options.Advanced);

            await Durability.StartAsync(Options.Advanced.Cancellation);
        }
    }
}
