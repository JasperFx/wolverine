using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Util;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    private bool _hasStarted;
    private Task? _idleAgentCleanupLoop;

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

            // Check for a source-generated type loader to bypass runtime assembly scanning
            var typeLoader = _container.Services.GetService(typeof(IWolverineTypeLoader)) as IWolverineTypeLoader;
            if (typeLoader == null)
            {
                // Also check for the assembly-level attribute as a discovery mechanism
                typeLoader = tryDiscoverTypeLoaderFromAttribute();
            }

            if (typeLoader != null)
            {
                Logger.LogInformation(
                    "Source-generated IWolverineTypeLoader detected, using compile-time discovery to reduce startup time");
                Handlers.UseTypeLoader(typeLoader);
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
                    await loadAgentRestrictionsAsync();
                    await startMessagingTransportsAsync();
                    startInMemoryScheduledJobs();
                    await startNodeAgentWorkflowAsync();
                    _idleAgentCleanupLoop = Task.Run(executeIdleSendingAgentCleanup, Cancellation);
                    break;
                case DurabilityMode.Solo:
                    await startMessagingTransportsAsync();
                    startInMemoryScheduledJobs();
                    _idleAgentCleanupLoop = Task.Run(executeIdleSendingAgentCleanup, Cancellation);
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

            // Subscribe to the host shutdown signal so we can immediately latch all receivers
            // the moment SIGTERM/ApplicationStopping fires, rather than waiting until our
            // IHostedService.StopAsync is called (which may be delayed by other hosted services)
            try
            {
                var lifetime = _container.Services.GetService(typeof(IHostApplicationLifetime)) as IHostApplicationLifetime;
                lifetime?.ApplicationStopping.Register(OnApplicationStopping);
            }
            catch (Exception e)
            {
                Logger.LogDebug(e, "Could not subscribe to IHostApplicationLifetime.ApplicationStopping");
            }
        }
        catch (Exception? e)
        {
            MessageTracking.LogException(e, message: "Failed to start the Wolverine messaging");
            throw;
        }
    }

    internal void OnApplicationStopping()
    {
        Logger.LogInformation("Application stopping signal received");
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

        if (DynamicCodeBuilder.WithinCodegenCommand)
        {
            // Don't do anything here
            return;
        }

        await _agentCancellation.CancelAsync();

        _hasStopped = true;

        // Latch health checks ASAP
        DisableHealthChecks();

        _idleAgentCleanupLoop?.SafeDispose();

        if (StopMode == StopMode.Normal)
        {
            // Step 1: Drain endpoints — each listener is stopped, its receiver latched,
            // then in-flight handlers are drained. Receivers are not latched up front,
            // since messages might be unnecessarily deferred before listeners are stopped.
            await _endpoints.DrainAsync();

            if (_accumulator.IsValueCreated)
            {
                await _accumulator.Value.DrainAsync();
            }
        }

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
                // Release any ownership on the way out. Do this *after* draining endpoints
                // so in-flight messages complete before their ownership is released.
                await _stores.Value.ReleaseAllOwnershipAsync(DurabilitySettings.AssignedNodeNumber);
            }
            catch (ObjectDisposedException)
            {
                // This could happen if DisposeAsync() is called before StopAsync()
            }
        }

        if (StopMode == StopMode.Normal)
        {
            // Step 2: Now teardown agents — safe after endpoints drained and ownership released
            await teardownAgentsAsync();
        }

        DurabilitySettings.Cancel();

        // ReSharper disable once SuspiciousTypeConversion.Global
        if (Observer is IAsyncDisposable d)
        {
            await d.DisposeAsync();
        }
    }

    private async Task loadAgentRestrictionsAsync()
    {
        if (Storage is NullMessageStore) return;
        var state = await Storage.Nodes.LoadNodeAgentStateAsync(Cancellation);
        Restrictions = state.Restrictions;
    }

    private void startInMemoryScheduledJobs()
    {
        ScheduledJobs =
            new InMemoryScheduledJobProcessor((ILocalQueue)Endpoints.AgentForLocalQueue(TransportConstants.Replies), Logger);

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

        // Pre-compute message type names for global partitioning interceptor
        // This handles MessagesImplementing<T>(), namespace, and assembly scopes
        // that can't be resolved from a string alone
        if (Options.MessagePartitioning.GlobalPartitionedTopologies.Count > 0)
        {
            var knownMessageTypes = Handlers.Chains.Select(x => x.MessageType).ToList();
            foreach (var topology in Options.MessagePartitioning.GlobalPartitionedTopologies)
            {
                topology.ResolveMessageTypeNames(knownMessageTypes);
            }
        }

        // Build message-type-to-ancillary-store mapping for durable inbox routing.
        // When a handler targets an ancillary store on a different database, incoming
        // envelopes should be persisted in that store for transactional atomicity.
        if (Stores != null && Stores.HasAnyAncillaryStores())
        {
            foreach (var chain in Handlers.Chains.Where(c => c.AncillaryStoreType != null))
            {
                var messageTypeName = chain.MessageType.ToMessageTypeName();
                Stores.MapMessageTypeToAncillaryStore(messageTypeName, chain.AncillaryStoreType!);
            }
        }

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

    private async Task executeIdleSendingAgentCleanup()
    {
        while (!Cancellation.IsCancellationRequested)
        {
            await Task.Delay(Options.Durability.SendingAgentIdleTimeout, Cancellation);
            try
            {
                var idleTimeout = Options.Durability.SendingAgentIdleTimeout;
                var cutoff = DateTimeOffset.UtcNow.Subtract(idleTimeout);

                foreach (var agent in _endpoints.ActiveSendingAgents().ToArray())
                {
                    if (agent.Endpoint is LocalQueue) continue;
                    if (agent.Endpoint.AutoStartSendingAgent()) continue;
                    if (agent.LastMessageSentAt > cutoff) continue;

                    Logger.LogInformation("Removing idle sending agent for {Destination}", agent.Destination);
                    await _endpoints.RemoveSendingAgentAsync(agent.Destination);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Logger.LogError(e, "Error cleaning up idle sending agents");
            }
        }
    }

    private void discoverListenersFromConventions()
    {
        // Let any registered routing conventions discover listener endpoints
        var handledMessageTypes = Handlers.Chains.Select(x => x.MessageType).ToList();

        // Include batch element types so that conventional routing creates listeners for
        // the element type (e.g., BatchedItem) rather than only the array type (BatchedItem[])
        foreach (var batch in Options.BatchDefinitions)
        {
            if (!handledMessageTypes.Contains(batch.ElementType))
            {
                handledMessageTypes.Add(batch.ElementType);
            }
        }
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

    private IWolverineTypeLoader? tryDiscoverTypeLoaderFromAttribute()
    {
        try
        {
            var assembly = Options.ApplicationAssembly;
            if (assembly == null) return null;

            var attribute = assembly.GetCustomAttributes(typeof(WolverineTypeManifestAttribute), false)
                .FirstOrDefault() as WolverineTypeManifestAttribute;

            if (attribute?.LoaderType == null) return null;

            return Activator.CreateInstance(attribute.LoaderType) as IWolverineTypeLoader;
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to instantiate source-generated IWolverineTypeLoader from assembly attribute, falling back to runtime scanning");
            return null;
        }
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
