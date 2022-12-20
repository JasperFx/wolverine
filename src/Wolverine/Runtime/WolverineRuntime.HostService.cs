using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Build up the message handlers
            await Handlers.CompileAsync(Options, _container);

            if (Options.AutoBuildEnvelopeStorageOnStartup && Storage is not NullMessageStore)
            {
                await Storage.Admin.MigrateAsync();
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
        if (Storage is NullMessageStore p)
        {
            p.ScheduledJobs = ScheduledJobs;
        }
    }

    private async Task startMessagingTransportsAsync()
    {
        discoverListenersFromConventions();
        
        foreach (var transport in Options.Transports)
        {
            if (!Options.Advanced.StubAllExternalTransports)
            {
                await transport.InitializeAsync(this).ConfigureAwait(false);
            }
            else
            {
                Logger.LogInformation("'Stubbing' out all external transports for testing");
            }
            
            foreach (var endpoint in transport.Endpoints())
            {
                endpoint.Runtime = this; // necessary to locate serialization
                endpoint.Compile(this);
            }
        }

        foreach (var transport in Options.Transports)
        {
            var replyUri = transport.ReplyEndpoint()?.Uri;

            foreach (var endpoint in transport.Endpoints().Where(x => x.AutoStartSendingAgent()))
            {
                endpoint.StartSending(this, replyUri);
            }
        }

        if (!Options.Advanced.StubAllExternalTransports)
        {
            await Endpoints.StartListenersAsync();
        }
        else
        {
            Logger.LogInformation("All external endpoint listeners are disabled because of configuration");
        }
    }

    private void discoverListenersFromConventions()
    {
        // Let any registered routing conventions discover listener endpoints
        var handledMessageTypes = Handlers.Chains.Select(x => x.MessageType).ToList();
        if (!Options.Advanced.StubAllExternalTransports)
        {
            foreach (var routingConvention in Options.RoutingConventions)
            {
                routingConvention.DiscoverListeners(this, handledMessageTypes);
            }
        }
        else
        {
            Logger.LogInformation("External transports are disabled, skipping conventional listener discovery");
        }

        Options.LocalRouting.DiscoverListeners(this, handledMessageTypes);
    }

    private Task startDurabilityAgentAsync()
    {
        // HOKEY, BUT IT WORKS
        if (_container.Model.DefaultTypeFor<IMessageStore>() != typeof(NullMessageStore) &&
            Options.Advanced.DurabilityAgentEnabled)
        {
            var durabilityLogger = _container.GetInstance<ILogger<DurabilityAgent>>();

            // TODO -- use the worker queue for Retries?
            var worker = new DurableReceiver(new LocalQueue("scheduled"), this, Pipeline);

            Durability = new DurabilityAgent(this, Logger, durabilityLogger, worker, Storage,
                Options.Advanced);

            return Durability.StartAsync(Options.Advanced.Cancellation);
        }

        return Task.CompletedTask;
    }
}