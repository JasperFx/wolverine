using JasperFx;
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
        // Make this idempotent because the AddResourceSetupOnStartup() can cause it to bootstrap twice
        if (_hasStarted) return;
        
        try
        {
            Logger.LogInformation("Starting Wolverine messaging for application assembly {Assembly}",
                Options.ApplicationAssembly!.GetName());

            logCodeGenerationConfiguration();

            await ApplyAsyncExtensions();

            await _stores.Value.InitializeAsync();

            if (!Options.ExternalTransportsAreStubbed)
            {
                foreach (var configuresRuntime in Options.Transports.OfType<ITransportConfiguresRuntime>().ToArray())
                {
                    await configuresRuntime.ConfigureAsync(this);
                }
            }

            // Build up the message handlers
            Handlers.Compile(Options, _container);

            await tryMigrateStorage();

            // Has to be done before initializing the storage
            Handlers.AddMessageHandler(typeof(IAgentCommand), new AgentCommandHandler(this));
            
            if (Options.Durability.DurabilityAgentEnabled)
            {
                foreach (var store in await _stores.Value.FindAllAsync())
                {
                    store.Initialize(this);
                }
            }

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

    private bool _hasMigratedStorage;

    private async Task tryMigrateStorage()
    {
        if (_hasMigratedStorage) return;
        
        if (!Options.Durability.DurabilityAgentEnabled) return;
        
        if (Options.AutoBuildMessageStorageOnStartup != AutoCreate.None && Storage is not NullMessageStore)
        {
            await _stores.Value.MigrateAsync();
        }

        _hasMigratedStorage = true;
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

    public StopMode StopMode { get; set; } = StopMode.Normal;
    
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_hasStopped)
        {
            return;
        }

        await _agentCancellation.CancelAsync();

        _hasStopped = true;

        // Latch health checks ASAP
        DisableHealthChecks();
        
        if (_stores.IsValueCreated && StopMode == StopMode.Normal)
        {
            try
            {
                await _stores.Value.DrainAsync();
            }
            catch (TaskCanceledException)
            {
                // This can timeout, just swallow it here
            }

            try
            {
                // New to 3.0, try to release any ownership on the way out. Do this *after* the drain
                await _stores.Value.ReleaseAllOwnershipAsync(DurabilitySettings.AssignedNodeNumber);
            }
            catch (ObjectDisposedException)
            {
                // This could happen if DisposeAsync() is called before StopAsync()
            }
        }

        if (StopMode == StopMode.Normal)
        {
            // This MUST be called before draining the endpoints
            await teardownAgentsAsync();
            
            await _endpoints.DrainAsync();

            if (_accumulator.IsValueCreated)
            {
                await _accumulator.Value.DrainAsync();
            }
        }

        DurabilitySettings.Cancel();

        // ReSharper disable once SuspiciousTypeConversion.Global
        if (Observer is IAsyncDisposable d)
        {
            await d.DisposeAsync();
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
        // Start up metrics collection
        if (Options.Metrics.Mode != WolverineMetricsMode.SystemDiagnosticsMeter)
        {
            _accumulator.Value.Start();
        }
        
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
        Options.Durability.Mode = DurabilityMode.MediatorOnly;
        Options.LightweightMode = true;

        // So that you get valid information in the describe command and other diagnostics
        foreach (var endpoint in Options.Transports.AllEndpoints())
        {
            endpoint.Compile(this);
        }

        return StartAsync(CancellationToken.None);
    }
}

public enum StopMode
{
    Normal,
    
    /// <summary>
    /// Honestly, don't use this except in Wolverine testing...
    /// </summary>
    Quick
}