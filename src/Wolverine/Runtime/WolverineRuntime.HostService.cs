using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    private bool _hasStarted;
    private Task? _idleAgentCleanupLoop;

    /// <summary>
    /// Detects whether Wolverine is running in a metadata-only CLI mode (codegen, OpenAPI
    /// generation via GetDocument.Insider) where persistence and transport connectivity
    /// are not required. When detected, lightweight startup settings are applied automatically
    /// so the host can start without needing external databases or message brokers.
    /// </summary>
    private void applyMetadataOnlyModeIfDetected()
    {
        if (Options.LightweightMode) return; // Already applied (e.g., by StartLightweightAsync)

        var isMetadataOnly = DynamicCodeBuilder.WithinCodegenCommand
            || (Environment.GetEnvironmentVariable("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES")
                ?.Contains("GetDocument", StringComparison.OrdinalIgnoreCase) ?? false);

        if (!isMetadataOnly) return;

        Options.ExternalTransportsAreStubbed = true;
        Options.Durability.DurabilityAgentEnabled = false;
        Options.Durability.Mode = DurabilityMode.MediatorOnly;
        Options.LightweightMode = true;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Make this idempotent because the AddResourceSetupOnStartup() can cause it to bootstrap twice
        if (_hasStarted) return;

        // Auto-detect codegen and OpenAPI generation tools; suppress persistence/transport init
        applyMetadataOnlyModeIfDetected();

        try
        {
            Logger.LogInformation("Starting Wolverine messaging for application assembly {Assembly}",
                Options.ApplicationAssembly!.GetName());

            // GH-3521: surface the RememberedApplicationAssembly first-host-wins pin loudly. Buffered during
            // options configuration (no logger existed yet) and emitted here so a silently-inherited scanned
            // assembly in a multi-host test process is observable instead of a downstream "No routes" mystery.
            if (Options.ApplicationAssemblyReuseWarning is { } assemblyWarning)
            {
                Logger.LogWarning(assemblyWarning);
            }

            logCodeGenerationConfiguration();

            await ApplyAsyncExtensions();

            // Run after async extensions (which may mutate the options) and
            // before any listener is started so an early-arriving message
            // can't race the publish.
            EnvelopeSerializer.Limits = new EnvelopeReaderLimits(
                MaxBatchSize: Options.MaxIncomingEnvelopeBatchSize,
                MaxDataSize: Options.MaxIncomingEnvelopeDataSize,
                MaxHeaderCount: Options.MaxIncomingEnvelopeHeaderCount);
            WireProtocol.MaxFrameSize = Options.MaxIncomingTcpFrameSize;

            await _stores.Value.InitializeAsync();

            // AlwaysMakeScheduledMessagesDurable opts every non-durable scheduled send onto
            // the message store inbox; if no store is configured, we silently fall through
            // to in-process scheduling (lost on restart) — defeating the policy. Surface
            // this as a startup warning so a misconfiguration is observable rather than a
            // silent durability gap.
            if (Options.Durability.AlwaysMakeScheduledMessagesDurable && Storage is NullMessageStore)
            {
                Logger.LogWarning(
                    "Policies.AlwaysMakeScheduledMessagesDurable() is set but no message store is configured. " +
                    "Scheduled messages will continue to use in-process scheduling and will be lost on restart. " +
                    "Configure a message store (e.g. PersistMessagesWithPostgresql) to make the policy effective.");
            }

            if (!Options.ExternalTransportsAreStubbed)
            {
                foreach (var configuresRuntime in Options.Transports.OfType<ITransportConfiguresRuntime>().ToArray())
                {
                    await configuresRuntime.ConfigureAsync(this);
                }
            }

            // Build up the message handlers
            Handlers.Compile(Options, _container);

            // Under MultipleHandlerBehavior.Separated, a message type may have BOTH a direct
            // Handle(T) handler AND a BatchMessagesOf<T>() batch handler. By default the batch
            // local queue is the element type's convention queue — the SAME queue the direct
            // handler uses — so the two collide (a local queue resolves a single executor per
            // message type) and the batch is silently shadowed. Move the batch onto a dedicated
            // queue so both can run independently. Done before the messaging transports start so
            // the new queue still receives the durable/local-queue endpoint policies.
            reassignBatchQueuesThatCollideWithHandlers();

            // Under the DEFAULT Classic behavior the same collision is NOT resolved: the direct
            // Handle(T) handler wins and the BatchMessagesOf<T>() batch handler is silently shadowed.
            // Warn loudly (or throw, if opted in) so the shadowing is not a silent surprise. GH-3289.
            warnOrAssertBatchHandlerConflicts();

            // Apply BatchMessagesOf<T>(b => b.ProbeIndividuallyAfter(N)) as a failure rule on the batch
            // handler chain. GH-3289.
            applyBatchProbePolicies();

            // Point each batch at the partitioned topology its element type already belongs to, so
            // the assembled batch is sequenced against the unbatched handlers for its group id
            // rather than racing them on a separate queue. GH-3867. Must run before the transports
            // start so the chosen slot endpoints can still be flagged for an unbounded execution
            // block.
            resolveBatchExecutionTopologies();

            // GH-3973. Follow-up to GH-3867: say out loud when the batch could NOT be sequenced
            // against its unbatched siblings, because that leaves two concurrent writers and the
            // silent version of that is the bug. Must run AFTER resolveBatchExecutionTopologies,
            // which is what decides whether a topology was found.
            warnOrAssertUnsequencedBatchExecution();

            // GH-4151. Last of the chain-shaping steps, so every chain that will ever exist -- including the
            // batch chains just moved above -- is checked. In TypeLoadMode.Static a missing pre-built type
            // used to surface on the first message of that type, from inside executor construction where no
            // failure policy can reach it. Fail the deploy here instead, before storage migration or any
            // listener starts.
            Handlers.AssertPreBuiltTypesExist(Options);

            // Pre-populate the message-type-name cache so the per-message ToMessageTypeName()
            // hot path inside Envelope construction never pays the first-occurrence reflection
            // cost (attribute reads, interface walks, generic-type pretty-printing).
            // See issue #1577 (cold-start optimizations).
            Wolverine.Util.WolverineMessageNaming.PrepopulateCache(Handlers.AllMessageTypes());

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
                Logger.LogInformation("Wolverine assigned node id for envelope persistence is {NodeNumber}", Options.Durability.AssignedNodeNumber);
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

            // Pre-populate the per-message-type router cache so the per-message
            // RoutingFor() hot path never pays the first-occurrence
            // CloseAndBuildAs over MessageRouter<T> / EmptyMessageRouter<T>.
            // Must happen AFTER the messaging transports start (so external-
            // transport route sources can resolve their endpoints), but
            // before RuntimeIsFullyStarted observers run. AOT pillar follow-up
            // #2769 (Option A).
            //
            // Skip in MediatorOnly and Serverless modes:
            //   - MediatorOnly: no messaging happens through this runtime, so
            //     RoutingFor() is never called in steady state. Pre-populating
            //     would lazily instantiate local sending agents (the
            //     LocalRoutingMessageSource resolves Endpoint.Agent as a side
            //     effect of building a route), violating the mode's "no
            //     transports" contract.
            //   - Serverless: RemoveLocal() above stripped the local transport,
            //     but MessageRouterBase<T>'s ctor unconditionally calls
            //     GetOrBuildSendingAgent(TransportConstants.DurableLocalUri)
            //     for scheduled-envelope fallback, which now throws
            //     UnknownTransportException. Skipping the pre-population avoids
            //     materializing routers we don't need in this mode; per-type
            //     RoutingFor() on the cold path still works because callers
            //     either target external endpoints directly or never invoke
            //     routing for local-only types.
            //
            // TODO: a follow-up could make MessageRouterBase<T>'s LocalDurableQueue
            // lazy / nullable so Serverless apps reclaim the AOT cold-start win.
            var mode = Options.Durability.Mode;
            if (mode != DurabilityMode.MediatorOnly && mode != DurabilityMode.Serverless)
            {
                PrepopulateRoutingCache(Handlers.AllMessageTypes());
            }

            await Observer.RuntimeIsFullyStarted();
            _hasStarted = true;

            // Freeze fault-publishing policy so per-type overrides cannot be silently
            // mutated from runtime code after host startup completes. All bootstrap
            // callbacks (UseWolverine + per-type PublishFault calls) have run by now.
            Options.FaultPublishing.Freeze();

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
            try
            {
                await _stores.Value.MigrateAsync();
            }
            catch (Exception e) when (Options.ResourceMigrationFailureMode == ResourceMigrationFailureMode.ContinueOnFailures)
            {
                // e.g. a replica that lost the migration lock during a rolling deploy. Log and keep
                // starting up rather than crash-looping. See GH-3130.
                Logger.LogError(e,
                    "Failed to migrate Wolverine message storage on startup. Continuing startup anyway because ResourceMigrationFailureMode is ContinueOnFailures.");
            }
        }
        else if (Storage is not NullMessageStore)
        {
            Logger.LogInformation(
                "Skipping automatic message storage migration on startup because AutoBuildMessageStorageOnStartup is None. The message storage must have been provisioned ahead of time, e.g. with 'resources setup' / IHost.SetupResources()");

            // None is a claim that something else provisioned the storage, so verify the claim here rather
            // than letting the first agent or listener to touch a missing table fail with a bare "relation
            // does not exist" from somewhere much further into startup - which names neither the storage
            // nor the setup step that was skipped. Same failure policy as the migration above, so a rolling
            // deploy that deliberately tolerates a replica starting ahead of its provisioning step still
            // can. Stores that cannot introspect their own schema no-op this (IMessageStoreAdmin default).
            //
            // GH-4166: this asks only whether the storage EXISTS. It deliberately does not run the full
            // Weasel schema diff -- that is the very work AutoCreate.None is set to avoid, and doing it
            // here timed out startup on small Azure SQL tiers in 6.30.0/6.30.1. Drift is likewise not a
            // startup failure: None means something else owns this schema.
            //
            // The whole collection rather than Storage alone, so that it covers what MigrateAsync above
            // covers: an ancillary store whose schema was never provisioned would otherwise go unchecked and
            // fail whenever something first used it.
            try
            {
                await _stores.Value.AssertStorageProvisionedAsync(Cancellation);
            }
            catch (Exception e) when (Options.ResourceMigrationFailureMode ==
                                      ResourceMigrationFailureMode.ContinueOnFailures)
            {
                Logger.LogError(e,
                    "The Wolverine message storage is missing or out of date and AutoBuildMessageStorageOnStartup is None. Continuing startup anyway because ResourceMigrationFailureMode is ContinueOnFailures.");
            }
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
                // Core WolverineFx no longer ships the Roslyn runtime compiler (#2876). Dynamic mode
                // always compiles handler/middleware dispatch at runtime, so it requires an
                // IAssemblyGenerator — auto-registered by referencing WolverineFx.RuntimeCompilation,
                // or via an explicit opts.UseRuntimeCompilation(). Fail fast with guidance if absent.
                if (!_container.HasRegistrationFor(typeof(IAssemblyGenerator)))
                {
                    throw new InvalidOperationException(
                        "Wolverine is running in TypeLoadMode.Dynamic, which compiles handler/middleware code at runtime, " +
                        "but no IAssemblyGenerator (Roslyn) is registered. Core WolverineFx no longer ships the runtime compiler. " +
                        "Either add the 'WolverineFx.RuntimeCompilation' NuGet package (it auto-registers when referenced, or call " +
                        "opts.UseRuntimeCompilation() in UseWolverine(...)), or pre-generate code with 'dotnet run -- codegen write' " +
                        "and set opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static. " +
                        "See https://wolverinefx.net/guide/codegen.html (GH-2876).");
                }

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
    
    /// <summary>
    ///     Shut Wolverine down. Single-entry and joinable: the first caller drives the shutdown and every
    ///     later caller gets a task that completes when THAT shutdown has finished.
    /// </summary>
    /// <remarks>
    ///     Both IHostedService.StopAsync and IAsyncDisposable.DisposeAsync route in here. The claim has to
    ///     happen before the first await: this used to read a "have I stopped" flag, await the agent
    ///     cancellation, and only then set the flag, so two callers could each see "not stopped", pass the
    ///     guard, and run this whole method concurrently. Nothing below is written for that --
    ///     teardownAgentsAsync in particular nulls the fields it has just disposed, so the second pass
    ///     found them cleared and threw NullReferenceException out of IHost.StopAsync instead of quietly
    ///     no-opping.
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (DynamicCodeBuilder.WithinCodegenCommand)
        {
            // Don't do anything here, and leave the runtime unclaimed for a later real shutdown
            return Task.CompletedTask;
        }

        var inFlight = Volatile.Read(ref _stopped);
        if (inFlight != null)
        {
            return inFlight;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        inFlight = Interlocked.CompareExchange(ref _stopped, completion.Task, null);
        if (inFlight != null)
        {
            return inFlight;
        }

        return runShutdownAsync(completion);
    }

    private async Task runShutdownAsync(TaskCompletionSource completion)
    {
        try
        {
            await shutdownAsync();
        }
        finally
        {
            // Joiners are waiting to know the shutdown has finished, not to be told a second time about
            // a failure that was already reported to whoever drove it -- disposal is a joiner, and
            // turning one failed stop into a failed Dispose() as well helps nobody.
            completion.TrySetResult();
        }
    }

    private async Task shutdownAsync()
    {
        await _agentCancellation.CancelAsync();

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
            catch (OperationCanceledException)
            {
                // Best-effort drain. TaskCanceledException is the common shape, but
                // some ADO.NET drivers (Npgsql, MySqlConnector) raise a plain
                // OperationCanceledException when the host's shutdown timeout fires
                // mid-command — catch the parent so we cover both.
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
            catch (OperationCanceledException)
            {
                // Best-effort cleanup — when the host's shutdown timeout fires (or the
                // caller passes an already-cancelled token through Host.StopAsync),
                // every persistence command picks up the cancellation and the SQL
                // driver throws OperationCanceledException. Swallow it: any envelopes
                // left as owner_id = node_id will be reclaimed by the durability
                // agent's recovery polling on the next live node, so dropping the
                // release here is functionally safe and avoids surfacing a normal
                // shutdown race as a test/run failure. Same reasoning as GH-2671.
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
        //
        // Use AllChains() rather than Chains so per-endpoint sticky chains
        // produced by MultipleHandlerBehavior.Separated are included.
        // Without this, [MartenStore]-attributed handlers under Separated mode
        // never make it into the map and inbox routing falls back to the main
        // store. See https://github.com/JasperFx/wolverine/issues/2576.
        if (Stores != null && Stores.HasAnyAncillaryStores())
        {
            var markerTypes = Stores.AncillaryMarkerTypes().ToArray();

            foreach (var chain in Handlers.AllChains())
            {
                var storeType = chain.AncillaryStoreType ?? inferAncillaryStoreType(chain, markerTypes);
                var messageTypeName = chain.MessageType.ToMessageTypeName();

                // A sticky chain speaks only for its own endpoints. Record it per endpoint as well,
                // because one message type handled by several sticky handlers targeting different
                // stores collapses onto a single key in the message type keyed map below, where the
                // last chain registered silently wins for every endpoint. Registered even when
                // storeType is null so that a sticky chain on the main store is not handed a sibling
                // endpoint's ancillary store by the fallback. See GH-3886.
                foreach (var endpoint in chain.Endpoints)
                {
                    Stores.MapEndpointMessageTypeToAncillaryStore(endpoint.Uri, messageTypeName, storeType);
                }

                if (storeType == null) continue;

                Stores.MapMessageTypeToAncillaryStore(messageTypeName, storeType);
            }
        }

        // No local queues if running in Serverless
        if (Options.Durability.Mode == DurabilityMode.Serverless)
        {
            Options.Transports.RemoveLocal();
        }

        var failedTransports = new List<ITransport>();
        foreach (var transport in Options.Transports)
        {
            if (!Options.ExternalTransportsAreStubbed)
            {
                try
                {
                    await transport.InitializeAsync(this).ConfigureAwait(false);
                }
                catch (Exception e) when (Options.ResourceMigrationFailureMode == ResourceMigrationFailureMode.ContinueOnFailures)
                {
                    // e.g. a transient broker-provisioning failure during a rolling deploy. Log, skip this
                    // transport's endpoint startup below, and keep the application starting. See GH-3130.
                    failedTransports.Add(transport);
                    Logger.LogError(e,
                        "Failed to initialize Wolverine transport {Transport} on startup. Continuing startup anyway because ResourceMigrationFailureMode is ContinueOnFailures.",
                        transport);
                }
            }
            else
            {
                Logger.LogInformation("'Stubbing' out all external Wolverine transports for testing");
            }
        }

        // GH-3712. Say out loud when a listening endpoint's mode ignores the settings it was given --
        // most importantly Inline together with PartitionProcessingByGroupId(), which silently dropped
        // the group id ordering guarantee. Runs even when the external transports are stubbed, so a
        // test host surfaces the same misconfiguration a deployed one would.
        //
        // Deliberately ahead of the sending agents below: an Inline local queue cannot build one at all,
        // and LocalQueue.BuildAgent()'s throw is a much worse error message than the validator's. See GH-4022.
        validateListenerConfiguration();

        foreach (var transport in Options.Transports)
        {
            // A transport that failed to initialize under ContinueOnFailures has no usable endpoints
            if (failedTransports.Contains(transport)) continue;

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

    /// <summary>
    /// GH-3712. Compile every listening endpoint and check its settings against its mode. Compilation has to
    /// happen first, because endpoint policies and delayed configuration are what settle the final mode --
    /// and it is idempotent, so the later StartListenerAsync() call is unaffected.
    /// </summary>
    private void validateListenerConfiguration()
    {
        var listeners = Options.Transports
            .SelectMany(x => x.Endpoints())
            .Where(x => x.IsListener || x is LocalQueue)
            .ToArray();

        foreach (var endpoint in listeners)
        {
            endpoint.Compile(this);
        }

        // GH-4060. Requeue policies are configured on the handler graph -- globally and per chain -- rather than on
        // an endpoint, so the endpoint-scoped rules cannot see them. Handlers.Compile() has already run by this
        // point, so both levels are settled and this is a straight read.
        var requeuePoliciesConfigured = Handlers.Failures.AnyRequeuePolicies()
                                        || Handlers.Chains.Any(x => x.Failures.AnyRequeuePolicies());

        ListenerConfigurationValidator.AssertValid(listeners, Logger, requeuePoliciesConfigured);
    }

    /// <summary>
    /// Associate a handler chain with an ancillary store it never names with an attribute. A handler
    /// that takes an enrolled EF Core DbContext as a dependency targets that store's database just as
    /// surely as one marked with [Storage], but carries nothing for StorageAttributeEagerPolicy to
    /// find -- so its inbox envelope landed in the main store while the handler committed to the
    /// ancillary one, leaving the envelope permanently Incoming and outside the handler's
    /// transaction. See https://github.com/JasperFx/wolverine/issues/3870.
    ///
    /// The chain's own persistence provider is the only thing that can say who owns its transaction,
    /// so it gets the deciding vote through IPersistenceFrameProvider.TryDetermineTransactionOwnerType.
    /// EF Core answers with the DbContext its transactional middleware picked, so the inbox row lands
    /// in the same database as the SaveChanges. Every other provider answers null, because their
    /// ancillary stores are named with [Storage]/[MartenStore]/[PolecatStore] -- which has already
    /// populated AncillaryStoreType above -- and merely depending on a store interface says nothing
    /// about who commits. Inferring from the dependency graph alone dragged the inbox, and with it
    /// dead-lettering, away from a Marten handler that used an ancillary store purely for read-only
    /// reference queries. See https://github.com/JasperFx/wolverine/issues/3953.
    /// </summary>
    private Type? inferAncillaryStoreType(HandlerChain chain, Type[] markerTypes)
    {
        if (markerTypes.Length == 0) return null;

        // Cheap pre-filter: no marker anywhere in the dependency graph means there is nothing to infer,
        // and the provider (which may build DbContexts to answer) never has to be consulted.
        var dependencies = chain.ServiceDependencies(_container, Type.EmptyTypes).ToArray();
        if (!markerTypes.Any(dependencies.Contains)) return null;

        try
        {
            var owner = Options.CodeGeneration.GetPersistenceProviders(chain, _container)
                .TryDetermineTransactionOwnerType(chain, _container);

            return owner != null && markerTypes.Contains(owner) ? owner : null;
        }
        catch (Exception e)
        {
            // Inbox routing must never take the application down at startup. Whatever a provider cannot
            // answer here it will answer -- or fail loudly and specifically about -- at codegen time.
            Logger.LogDebug(e,
                "Unable to determine the transaction owner of {Chain} while mapping handlers to ancillary message stores. Its inbox will use the main store.",
                chain.Description);

            return null;
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

                    // GH-1908 was aimed at ephemeral control and reply queues, and those are never
                    // durable. A durable endpoint's sending agent owns the outbox drain for its
                    // destination, so reaping it strands every envelope staged for that destination
                    // until something rebuilds the agent -- and an endpoint reached only through
                    // EndpointFor(uri) has no subscriptions, so nothing here recognized it as
                    // load bearing. See https://github.com/JasperFx/wolverine/issues/3955.
                    if (agent.Endpoint.Mode == EndpointMode.Durable) continue;

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

    // Suffix appended to the element type's convention queue name to host the batch processor
    // when the same element type also has a direct handler under Separated mode.
    internal const string BatchQueueSuffix = "-batch";

    private void reassignBatchQueuesThatCollideWithHandlers()
    {
        if (Options.MultipleHandlerBehavior != MultipleHandlerBehavior.Separated)
        {
            return;
        }

        if (Options.BatchDefinitions.Count == 0)
        {
            return;
        }

        var local = Options.Transports.GetOrCreate<LocalTransport>();

        foreach (var batch in Options.BatchDefinitions)
        {
            // No direct Handle(T) handler for the element type -> the batch owns the element
            // type's queue and the existing fallback routing/executor behavior is correct.
            if (Handlers.ChainFor(batch.ElementType) == null)
            {
                continue;
            }

            // The user explicitly pointed the batch at a distinct queue already -> respect it.
            var directQueue = local.FindQueueForMessageType(batch.ElementType);
            if (!string.Equals(batch.LocalExecutionQueueName, directQueue.EndpointName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Move the batch onto a dedicated queue distinct from the direct handler's queue.
            var batchQueueName = directQueue.EndpointName + BatchQueueSuffix;
            var batchQueue = local.QueueFor(batchQueueName);
            batchQueue.Mode = directQueue.Mode;

            // Wolverine's own reassignment, not a user choice — see SetDefaultLocalExecutionQueueName.
            batch.SetDefaultLocalExecutionQueueName(batchQueue.EndpointName);
        }
    }

    private void warnOrAssertBatchHandlerConflicts()
    {
        // Only relevant under the default Classic behavior. Under Separated the same collision is
        // legitimately resolved by reassignBatchQueuesThatCollideWithHandlers() (both handlers run).
        if (Options.MultipleHandlerBehavior != MultipleHandlerBehavior.ClassicCombineIntoOneLogicalHandler)
        {
            return;
        }

        if (Options.BatchDefinitions.Count == 0)
        {
            return;
        }

        foreach (var batch in Options.BatchDefinitions)
        {
            // A non-null chain for the element type itself means there is a direct Handle(T) handler
            // colliding with the batch (the batch handler is for T[], a different chain).
            var directChain = Handlers.ChainFor(batch.ElementType);
            if (directChain == null)
            {
                continue;
            }

            var elementType = batch.ElementType.NameInCode();
            var directHandlers = directChain.Handlers
                .Select(x => $"{x.HandlerType.NameInCode()}.{x.Method.Name}()").Join(", ");

            var message =
                $"Batch handler conflict for message type '{batch.ElementType.FullNameInCode()}': it has BOTH a direct handler ({directHandlers}) " +
                $"and a BatchMessagesOf<{elementType}>() batch handler ({elementType}[]). Under the default " +
                $"MultipleHandlerBehavior.ClassicCombineIntoOneLogicalHandler the direct handler wins and the batch handler is silently " +
                $"shadowed (it never runs). To run both independently set opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated; " +
                $"otherwise remove one of the two handlers. (Call opts.AssertNoBatchHandlerConflicts() to make Wolverine throw on this instead of warning.)";

            if (Options.AssertsNoBatchHandlerConflicts)
            {
                throw new InvalidOperationException(message);
            }

            Logger.LogWarning(message);
        }
    }

    /// <summary>
    /// GH-3973. A batched element type that ALSO has unbatched handlers has two independent execution
    /// paths writing the same entity: the assembled batch on its own local queue, and the unbatched
    /// siblings on the listener receiver's own execution block. A partitioned topology resolves that —
    /// <see cref="resolveBatchExecutionTopologies" /> points the batch at the same slots, so every writer
    /// for one group id is genuinely a single writer. Without one, nothing sequences them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Sequential()</c> on the batch queue does not close this: it serializes the batch against
    ///         itself and against nothing else.
    ///     </para>
    ///     <para>
    ///         The asymmetry is what makes this worth a startup message rather than documentation. With a
    ///         <c>GlobalPartitioned</c> topology the configuration is safe; without one — embedded hosts,
    ///         single-node deployments, most test fixtures — the same code has two concurrent writers to one
    ///         event stream, and it surfaces as intermittent stream-version collisions under load. A defect
    ///         is therefore unreachable in the configuration the tests use and reachable in the one that
    ///         ships.
    ///     </para>
    /// </remarks>
    private void warnOrAssertUnsequencedBatchExecution()
    {
        if (Options.BatchDefinitions.Count == 0)
        {
            return;
        }

        // Under Classic the direct handler wins and the batch never runs at all, so there is only ever
        // one writer. warnOrAssertBatchHandlerConflicts() covers that shape.
        if (Options.MultipleHandlerBehavior != MultipleHandlerBehavior.Separated)
        {
            return;
        }

        foreach (var batch in Options.BatchDefinitions)
        {
            // Sequenced onto a shared partitioned topology by GH-3867 -- single writer per group id.
            if (batch.ExecutionSlots is { Count: > 0 })
            {
                continue;
            }

            // No unbatched sibling means no second writer.
            var directChain = Handlers.ChainFor(batch.ElementType);
            if (directChain == null)
            {
                continue;
            }

            var elementType = batch.ElementType.NameInCode();
            var directHandlers = directChain.Handlers
                .Select(x => $"{x.HandlerType.NameInCode()}.{x.Method.Name}()").Join(", ");

            var message =
                $"Unsequenced batch execution for message type '{batch.ElementType.FullNameInCode()}': it has BOTH unbatched handlers ({directHandlers}) " +
                $"and a BatchMessagesOf<{elementType}>() batch handler ({elementType}[]), and Wolverine could not sequence them against each other. " +
                $"The assembled batch executes on local queue '{batch.LocalExecutionQueueName}' while the unbatched handlers execute on the listener's own " +
                $"execution block, so both may write the same entity concurrently -- which typically surfaces as intermittent concurrency or stream-version " +
                $"collisions under load rather than as an obvious failure. " +
                $"To sequence them, put '{elementType}' into a GlobalPartitioned message topology, which lets Wolverine choose the batch's execution slot from " +
                $"the batch's group id. Note Sequential() on the batch queue does NOT close this: it serializes the batch against itself only. " +
                $"(Call opts.AssertBatchExecutionIsSequenced() to make Wolverine throw on this instead of warning.)";

            if (Options.AssertsBatchExecutionIsSequenced)
            {
                throw new InvalidOperationException(message);
            }

            Logger.LogWarning(message);
        }
    }

    /// <summary>
    /// GH-3867. When a batched element type already belongs to a partitioned message topology, the
    /// unbatched handlers for a given group id are being sequenced onto one topology slot while the
    /// assembled batch goes to its own dedicated local queue — a different execution block, so the
    /// batch races the very handlers the topology just sequenced. Point the batch at the same
    /// topology so its slot is chosen from the batch's group id.
    /// </summary>
    private void resolveBatchExecutionTopologies()
    {
        if (Options.BatchDefinitions.Count == 0)
        {
            return;
        }

        foreach (var batch in Options.BatchDefinitions)
        {
            // Naming a queue, or asking for a dedicated one outright, is an explicit choice to run
            // the batches somewhere of your own choosing. Respect it.
            if (batch.IsLocalQueueExplicit || batch.ForcesDedicatedQueue)
            {
                continue;
            }

            var slots = findPartitionedExecutionSlots(batch.ElementType);
            if (slots == null)
            {
                continue;
            }

            batch.ExecutionSlots = slots;

            foreach (var slot in slots)
            {
                // These queues now receive the batches produced by the very messages they execute,
                // which closes a cycle through the batching channel's bounded buffers. Same
                // reasoning as GH-3287's unbounded local queues; see Endpoint.HostsBatchExecution.
                slot.HostsBatchExecution = true;
            }

            // Slotting a batch requires the batch to belong to exactly one group, which the default
            // tenant-only batcher cannot promise. Swap it for the group-id batcher — but leave a
            // batcher the application supplied alone, since it may stamp group ids itself.
            var batcherType = batch.Batcher.GetType();
            if (batcherType.IsGenericType && batcherType.GetGenericTypeDefinition() == typeof(DefaultMessageBatcher<>))
            {
                batch.GroupByGroupId();
            }
        }
    }

    /// <summary>
    /// The local queues an element type's partitioned topology executes on, or null when it does not
    /// belong to one. A global topology's companion local queues win over a purely local sharded
    /// topology because global partitioning is the stronger guarantee of the two.
    /// </summary>
    private IReadOnlyList<Endpoint>? findPartitionedExecutionSlots(Type elementType)
    {
        var partitioning = Options.MessagePartitioning;

        if (partitioning.TryFindGlobalTopology(elementType, out var global) && global!.LocalTopology != null)
        {
            return global.LocalTopology.Slots;
        }

        if (partitioning.TryFindTopology(elementType, out var topology) &&
            topology is Partitioning.LocalPartitionedMessageTopology local)
        {
            return local.Slots;
        }

        // A sharded topology over an EXTERNAL transport has no local queue to enqueue a batch onto —
        // its unbatched handlers execute inside the listener's own sharded block. Nothing to target.
        return null;
    }

    private void applyBatchProbePolicies()
    {
        if (Options.BatchDefinitions.Count == 0)
        {
            return;
        }

        foreach (var batch in Options.BatchDefinitions)
        {
            if (batch.ProbeIndividuallyAfterAttempts is not { } attempts)
            {
                continue;
            }

            // The failure rule lives on the batch handler chain (the T[] handler), matching any exception:
            // retry the whole batch until it has failed `attempts` times, then re-run each member as its
            // own size-1 batch so only the failing one dead-letters.
            var batchChain = Handlers.ChainFor(batch.Batcher.BatchMessageType);
            if (batchChain == null)
            {
                continue;
            }

            batchChain.OnException<Exception>()
                .ContinueWith(new Batching.ProbeIndividuallyContinuationSource(attempts));
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

            // ALSO pre-register sender subscription metadata for each handled
            // message type so that endpoint policies (e.g.
            // UseDurableOutboxOnAllSendingEndpoints) apply to conventionally-
            // routed sender endpoints. Without this, transports like RabbitMQ
            // create the sender endpoint as a side effect of listener
            // discovery (ApplyListenerRoutingDefaults), but the Subscription
            // metadata used by AllSenders policies is added lazily by
            // DiscoverSenders only on the first publish — by which point
            // BrokerTransport.InitializeAsync has already Compile()'d the
            // endpoint with no subscriptions and Endpoint._hasCompiled
            // short-circuits the policy from ever applying. See GH-2588.
            //
            // PreregisterSenders is intentionally lighter than DiscoverSenders
            // — it does NOT build the sending agent (which would need a live
            // broker connection that hasn't been opened yet). The full
            // DiscoverSenders still runs lazily on first publish via
            // RoutingFor; by then the endpoint has already been compiled with
            // the subscription in place, so the policy decisions stick.
            foreach (var routingConvention in Options.RoutingConventions)
            {
                routingConvention.PreregisterSenders(handledMessageTypes, this);
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
