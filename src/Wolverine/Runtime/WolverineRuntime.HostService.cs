using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    private NodeAgentController? _agents;
    private bool _hasStarted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Logger.LogInformation("Starting Wolverine messaging for application assembly {Assembly}",
                Options.ApplicationAssembly!.GetName());

            // Build up the message handlers
            Handlers.Compile(Options, _container);

            if (Options.AutoBuildEnvelopeStorageOnStartup && Storage is not NullMessageStore)
            {
                await Storage.Admin.MigrateAsync();
            }

            // Has to be done before initializing the storage
            Handlers.AddMessageHandler(typeof(IAgentCommand), new AgentCommandHandler(this));
            await Storage.InitializeAsync(this);

            await startMessagingTransportsAsync();

            startInMemoryScheduledJobs();

            await startAgentsAsync();

            _hasStarted = true;
        }
        catch (Exception? e)
        {
            MessageTracking.LogException(e, message: "Failed to start the Wolverine messaging");
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

        await Storage.DrainAsync();

        // This MUST be called before draining the endpoints
        await teardownAgentsAsync();

        await _endpoints.DrainAsync();

        DurabilitySettings.Cancel();
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
            if (!Options.ExternalTransportsAreStubbed)
            {
                await transport.InitializeAsync(this).ConfigureAwait(false);
            }
            else
            {
                Logger.LogInformation("'Stubbing' out all external Wolverine transports for testing");
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
                endpoint.StartSending(this, replyUri);
        }

        if (!Options.ExternalTransportsAreStubbed)
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
        if (!Options.ExternalTransportsAreStubbed)
        {
            foreach (var routingConvention in Options.RoutingConventions)
                routingConvention.DiscoverListeners(this, handledMessageTypes);
        }
        else
        {
            Logger.LogInformation("External transports are disabled, skipping conventional listener discovery");
        }

        Options.LocalRouting.DiscoverListeners(this, handledMessageTypes);
    }

    internal async Task StartLightweightAsync()
    {
        if (_hasStarted)
        {
            return;
        }

        Options.ExternalTransportsAreStubbed = true;
        Options.Durability.DurabilityAgentEnabled = false;

        await StartAsync(CancellationToken.None);
    }
}