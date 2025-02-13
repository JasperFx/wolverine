using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    private bool _hasStarted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Logger.LogInformation("Starting Wolverine messaging for application assembly {Assembly}",
                Options.ApplicationAssembly!.GetName());

            logCodeGenerationConfiguration();

            await ApplyAsyncExtensions();

            if (!Options.ExternalTransportsAreStubbed)
            {
                foreach (var configuresRuntime in Options.Transports.OfType<ITransportConfiguresRuntime>().ToArray())
                {
                    await configuresRuntime.ConfigureAsync(this);
                }
            }

            // Build up the message handlers
            Handlers.Compile(Options, _container);

            if (Options.AutoBuildMessageStorageOnStartup && Storage is not NullMessageStore)
            {
                await Storage.Admin.MigrateAsync();
            }

            // Has to be done before initializing the storage
            Handlers.AddMessageHandler(typeof(IAgentCommand), new AgentCommandHandler(this));
            Storage.Initialize(this);

            // This MUST be done before the messaging transports are started up
            _hasStarted = true; // Have to do this before you can use MessageBus
            await startAgentsAsync();

            if (Options.Durability.AssignedNodeNumber == 0)
            {
                throw new InvalidOperationException(
                    "This Wolverine node was not able to create a non-zero assigned node number");
            }
            else
            {
                Logger.LogInformation("Wolverine assigned node id for envelope persistence is {NodeId}", Options.Durability.AssignedNodeNumber);
            }

            switch (Options.Durability.Mode)
            {
                case DurabilityMode.Balanced:
                    await startMessagingTransportsAsync();
                    startInMemoryScheduledJobs();
                    await startNodeAgentWorkflowAsync();
                    break;
                case DurabilityMode.Solo:
                    await startMessagingTransportsAsync();
                    startInMemoryScheduledJobs();
                    break;

                case DurabilityMode.Serverless:
                    Options.Transports.RemoveLocal();
                    Options.Policies.DisableConventionalLocalRouting();
                    Options.Policies.Add(new ServerlessEndpointsMustBeInlinePolicy());

                    await startMessagingTransportsAsync();
                    break;

                case DurabilityMode.MediatorOnly:
                    break;
            }

            await Observer.RuntimeIsFullyStarted();
            _hasStarted = true;
        }
        catch (Exception? e)
        {
            MessageTracking.LogException(e, message: "Failed to start the Wolverine messaging");
            throw;
        }
    }

    private bool _hasAppliedAsyncExtensions = false;
    internal async Task ApplyAsyncExtensions()
    {
        if (_hasAppliedAsyncExtensions) return;

        var asyncExtensions = _container.GetAllInstances<IAsyncWolverineExtension>();
        foreach (var extension in asyncExtensions)
        {
            await extension.Configure(Options);
        }

        _hasAppliedAsyncExtensions = true;
    }

    public void WarnIfAnyAsyncExtensions()
    {
        if (!_hasAppliedAsyncExtensions && _container.HasRegistrationFor(typeof(IAsyncWolverineExtension)))
        {
            Logger.LogInformation($"This application has asynchronous Wolverine extensions registered, but they have not been applied yet. You may want to call IServiceCollection.{nameof(ApplyAsyncExtensions)}() before configuring Wolverine.HTTP");
        }
    }

    private void logCodeGenerationConfiguration()
    {
        switch (Options.CodeGeneration.TypeLoadMode)
        {
            case TypeLoadMode.Dynamic:
                Logger.LogInformation(
                    $"The Wolverine code generation mode is {nameof(TypeLoadMode.Dynamic)}. This is suitable for development, but you may want to opt into other options for production usage to reduce start up time and resource utilization.");
                Logger.LogInformation("See https://wolverine.netlify.app/guide/codegen.html for more information");
                break;

            case TypeLoadMode.Auto:
                Logger.LogInformation(
                    $"The Wolverine code generation mode is {nameof(TypeLoadMode.Auto)} with pre-generated types being loaded from {Options.CodeGeneration.ApplicationAssembly.FullName}.");
                Logger.LogInformation("See https://wolverine.netlify.app/guide/codegen.html for more information");
                break;

            case TypeLoadMode.Static:
                Logger.LogInformation(
                    $"The Wolverine code generation mode is {nameof(TypeLoadMode.Static)} with pre-generated types being loaded from {Options.CodeGeneration.ApplicationAssembly.FullName}.");
                Logger.LogInformation(
                    "See https://wolverine.netlify.app/guide/codegen.html for more information about debugging static type loading issues with Wolverine");
                break;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_hasStopped)
        {
            return;
        }

        _agentCancellation.Cancel();

        _hasStopped = true;

        // Latch health checks ASAP
        DisableHealthChecks();
        
        if (_persistence.IsValueCreated)
        {
            try
            {
                await Storage.DrainAsync();
            }
            catch (TaskCanceledException)
            {
                // This can timeout, just swallow it here
            }

            try
            {
                // New to 3.0, try to release any ownership on the way out. Do this *after* the drain
                await Storage.Admin.ReleaseAllOwnershipAsync(DurabilitySettings.AssignedNodeNumber);
            }
            catch (ObjectDisposedException)
            {
                // This could happen if DisposeAsync() is called before StopAsync()
            }
        }

        // This MUST be called before draining the endpoints
        await teardownAgentsAsync();

        await _endpoints.DrainAsync();

        DurabilitySettings.Cancel();

        try
        {
            // Do this to release pooled connections in Npgsql just in case
            await Storage.DisposeAsync();
        }
        catch (Exception)
        {
        }
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

        // No local queues if running in Serverless
        if (Options.Durability.Mode == DurabilityMode.Serverless)
        {
            Options.Transports.RemoveLocal();
        }

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
        }

        foreach (var transport in Options.Transports)
        {
            var replyUri = transport.ReplyEndpoint()?.Uri;

            foreach (var endpoint in transport.Endpoints().Where(x => x.AutoStartSendingAgent()))
            {
                // There are a couple other places where senders might be getting
                // started before this point, so latch to avoid double creations
                if (_endpoints.HasSender(endpoint.Uri)) continue;

                var agent = endpoint.StartSending(this, replyUri);
                _endpoints.StoreSendingAgent(agent);
            }
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

    internal Task StartLightweightAsync()
    {
        if (_hasStarted)
        {
            return Task.CompletedTask;
        }

        Options.ExternalTransportsAreStubbed = true;
        Options.Durability.DurabilityAgentEnabled = false;

        return StartAsync(CancellationToken.None);
    }
}