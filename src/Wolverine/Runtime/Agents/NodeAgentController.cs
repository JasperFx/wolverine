using System.Collections.Concurrent;
using JasperFx;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    public static readonly Uri LeaderUri = new("wolverine://leader");

    private readonly Dictionary<string, IAgentFamily>
        _agentFamilies = new();

    private readonly CancellationTokenSource _cancellation;
    private readonly ILogger _logger;
    private readonly INodeAgentPersistence _persistence;

    private readonly IWolverineRuntime _runtime;

    // May be valuable later
    #pragma warning disable CS0169
    private DateTimeOffset? _lastAssignmentCheck;
    #pragma warning restore CS0169
    private readonly IWolverineObserver _observer;
    private DateTimeOffset? _lastNodeAssignmentHealthCheckTrace;

    // GH-3604 / D2: the capabilities this node advertised at StartLocalAgentProcessingAsync. Kept so a node
    // whose row was deleted out from under it (peer ejection under churn) can re-register with its REAL
    // capabilities instead of an empty skeleton -- an empty-capability row is a candidate for nothing in
    // capability-matched distribution, which silently shrinks the cluster.
    private IReadOnlyList<Uri> _capabilities = Array.Empty<Uri>();

    // GH-3638: agents already reported as failed, so the sweep below and the restart suppression in
    // StartAgentAsync fire once per transition into failure rather than on every 30s tick. An entry is
    // dropped the moment the agent is seen running again, so a shard that recovers and later fails anew
    // reports the new failure.
    private readonly ConcurrentDictionary<Uri, byte> _reportedFailures = new();

    // 0=free, 1=busy; guards against concurrent DoHealthChecksAsync calls
    // from the heartbeat loop and a CheckAgentHealth message arriving
    // simultaneously. Prevents a race on _lastLockIndex / _lastLockETag in
    // lease-based backends (RavenDb, CosmosDb) that would corrupt the
    // leadership lock.
    private int _healthCheckGuard;

    private bool ShouldTraceHealthCheck()
    {
        if (!_runtime.DurabilitySettings.NodeAssignmentHealthCheckTracingEnabled)
        {
            return false;
        }

        if (_runtime.DurabilitySettings.NodeAssignmentHealthCheckTraceSamplingPeriod.HasValue)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastNodeAssignmentHealthCheckTrace.HasValue)
            {
                var elapsed = now - _lastNodeAssignmentHealthCheckTrace.Value;
                if (elapsed < _runtime.DurabilitySettings.NodeAssignmentHealthCheckTraceSamplingPeriod.Value)
                {
                    return false;
                }
            }

            _lastNodeAssignmentHealthCheckTrace = now;
        }

        return true;
    }

    internal NodeAgentController(IWolverineRuntime runtime,
        INodeAgentPersistence persistence,
        IEnumerable<IAgentFamily> agentControllers, ILogger logger, CancellationToken cancellation)
    {
        _observer = runtime.Observer;

        _runtime = runtime;
        _persistence = persistence;
        foreach (var agentController in agentControllers)
        {
            _agentFamilies[agentController.Scheme] = agentController;
        }

        // A node with messaging disabled runs event subscription agents and nothing else, so it
        // advertises no listener agents. See DurabilitySettings.MessagingEnabled.
        if (runtime.Options.Durability.Mode == DurabilityMode.Balanced && runtime.Options.Durability.MessagingEnabled)
        {
            _agentFamilies[ExclusiveListenerFamily.SchemeName] = new ExclusiveListenerFamily(runtime);
            _agentFamilies[LeaderPinnedListenerFamily.SchemeName] = new LeaderPinnedListenerFamily(runtime);

            // GH-2685: durable, dynamically-registered listener URIs (e.g. per-IoT-device
            // MQTT topics). Opt-in via Durability.EnableDynamicListeners — when off, the
            // family isn't instantiated and the listener-registry table is never queried.
            if (runtime.Options.Durability.EnableDynamicListeners)
            {
                _agentFamilies[DynamicListenerUriEncoding.SchemeName] =
                    new DynamicListenerAgentFamily(runtime);
            }
        }

        if (runtime.Options.Durability.DurabilityAgentEnabled && runtime.Options.Durability.MessagingEnabled)
        {
            _agentFamilies[_runtime.Stores.Scheme] = _runtime.Stores;
        }

        if (runtime.Options.Durability.MessagingEnabled)
        {
            foreach (var family in runtime.Options.Transports.OfType<IAgentFamilySource>().SelectMany(x => x.BuildAgentFamilySources(runtime)))
            {
                _agentFamilies[family.Scheme] = family;
            }
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _logger = logger;
    }

    /// <summary>
    ///     Whether this node registered an agent family for <paramref name="scheme" />, and therefore
    ///     advertises its agents. Test hook for the family wiring in the constructor.
    /// </summary>
    internal bool HasFamily(string scheme) => _agentFamilies.ContainsKey(scheme);

    public ConcurrentDictionary<Uri, IAgent> Agents { get; } = new();

    public bool HasStartedInSoloMode { get; private set; }

    internal void AddHandlers(WolverineRuntime runtime)
    {
        var handlers = runtime.Handlers;

        handlers.RegisterMessageType(typeof(StartAgent));
        handlers.RegisterMessageType(typeof(StartAgents));
        handlers.RegisterMessageType(typeof(AgentsStarted));
        handlers.RegisterMessageType(typeof(AgentsStopped));
        handlers.RegisterMessageType(typeof(StopAgent));
        handlers.RegisterMessageType(typeof(StopAgents));
    }

    public async Task StopAsync(IMessageBus messageBus)
    {
        await stopAllAgentsAsync();

        try
        {
            try
            {
                if (_persistence.HasLeadershipLock())
                {
                    await _persistence.ReleaseLeadershipLockAsync();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to release the leadership lock");
            }

            await _persistence.DeleteAsync(_runtime.Options.UniqueNodeId, _runtime.DurabilitySettings.AssignedNodeNumber);

            await _observer.NodeStopped();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to delete the exiting node from node persistence");
        }
    }

    private async Task stopAllAgentsAsync()
    {
        // GH-3604 / D3 (WO-7): drain every locally-running agent with bounded parallelism instead of one at a
        // time. On a node running a large agent universe (database-per-tenant Marten with thousands of
        // subscription shards) the old sequential loop could not finish inside a typical 30s SIGTERM grace
        // window, so k8s SIGKILLed the process mid-drain and abandoned unflushed daemon progression -->
        // ProgressionOutOfOrderException on the next start. A bounded fan-out makes the shutdown window usable.
        //
        // Deliberately NOT threading a cancellation token into StopAsync here (each still gets
        // CancellationToken.None as before): this is the shutdown path itself, and a cancelled drain would
        // leave agents half-stopped. Each StopAsync keeps its own try/catch so one wedged agent cannot abort
        // the drain of its peers.
        var dop = Math.Max(1, _runtime.Options.Durability.MaxAgentStopParallelism);
        var options = new ParallelOptions { MaxDegreeOfParallelism = dop };

        await Parallel.ForEachAsync(Agents.ToArray(), options, async (entry, _token) =>
        {
            try
            {
                await entry.Value.StopAsync(CancellationToken.None);
                Agents.TryRemove(entry.Key, out _);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to stop agent {AgentUri}", entry.Value.Uri);
            }
        });
    }

    private ValueTask<IAgent> findAgentAsync(Uri uri)
    {
        if (_agentFamilies.TryGetValue(uri.Scheme, out var controller))
        {
            return controller.BuildAgentAsync(uri, _runtime);
        }

        throw new ArgumentOutOfRangeException(nameof(uri), $"Unrecognized agent scheme '{uri.Scheme}'");
    }

    public async Task StartAgentAsync(Uri agentUri)
    {
        if (Agents.TryGetValue(agentUri, out var existing))
        {
            // Idempotent for an agent that is genuinely still running -- or one deliberately Paused by an
            // error backoff / blue-green side-effect gate, which owns its own resume schedule and must not
            // be fought here.
            if (existing.Status != AgentStatus.Stopped)
            {
                // GH-3604 (D4): even though the agent is already running here, still re-upsert the
                // assignment row. If a peer wiped this live node's node_assignments rows out from under it
                // (e.g. an ejection/resurrection under churn), the leader keeps re-emitting AssignAgent for
                // the same (uri, node) pair forever because the grid never re-learns what is actually
                // running -- this early return was the exact no-op that made the loss permanent. The upsert
                // makes the grid self-healing after any assignment-row loss without a needless stop/start.
                await upsertAssignmentAsync(agentUri);
                return;
            }

            // GH-3638: a shard the daemon stopped on a failure that will recur on the exact same event --
            // a poison event, a body it cannot deserialize, an event type this deployment doesn't know, or
            // a progression row two processes are fighting over -- must NOT be swept back up by the
            // GH-3519 wedge recovery below. Restarting it re-runs the identical failure every
            // reevaluation, and each restart resets the operator's view of when and where it broke. Leave
            // it stopped, and surface the reason instead so somebody can act on it.
            if (existing is IEventSubscriptionAgent subscription && !canSelfHeal(subscription.Failure))
            {
                await reportAgentPausedAsync(agentUri, subscription.Failure);
                return;
            }

            // GH-3519: the agent is still registered on this node but its underlying shard has stopped
            // (e.g. an event-subscription shard that lost a first-assignment startup race and wedged, or
            // whose daemon execution loop faulted). The old blanket ContainsKey short-circuit treated any
            // registered agent as healthy forever, so the recurring reevaluation never resurrected a
            // wedged one -- it just sat in the 30s retry loop reporting a stale Running. Evict the dead
            // registration -- stopping it first to release any lingering daemon-side shard state -- so the
            // start below actually re-drives it.
            _logger.LogInformation(
                "Agent {AgentUri} is still registered on node {NodeNumber} but its shard is stopped; restarting it",
                agentUri, _runtime.Options.Durability.AssignedNodeNumber);

            Agents.TryRemove(agentUri, out _);
            try
            {
                await existing.StopAsync(_cancellation.Token);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error stopping wedged agent {AgentUri} before restarting it", agentUri);
            }
        }

        var agent = await startWithRetriesAsync(agentUri);

        Agents[agentUri] = agent;

        // GH-3638: a start that succeeded supersedes whatever failure was last reported for this agent, so
        // a later failure alerts again instead of being swallowed as a duplicate of the old one.
        _reportedFailures.TryRemove(agentUri, out _);

        await upsertAssignmentAsync(agentUri);
    }

    /// <summary>
    /// Start an agent, retrying a failure a bounded number of times before giving up on this tick.
    ///
    /// <para>GH-3519: a first-assignment start races whatever the agent depends on coming up. The reported
    /// shape is a multi-store Marten host where one event-subscription shard — a different one on every
    /// boot — was evaluated before its store's high-water detection was running and failed; the daemon now
    /// says so in as many words (<c>ShardStartException</c>, JasperFx/jasperfx#534) and releases the
    /// half-started shard (jasperfx#540), so the very next attempt succeeds. Without a local retry that
    /// attempt only came on the next assignment reevaluation, so the loser of the race sat idle for a full
    /// CheckAssignmentPeriod while its high-water mark climbed — the "permanent 30-second retry loop" in
    /// the report.</para>
    ///
    /// <para>Each attempt goes back through <see cref="findAgentAsync" />: a faulted start may have left
    /// the family's agent object unusable, and the family owns whether a rebuild is a fresh object or the
    /// same one. The exception thrown after the last attempt is the LAST failure with its cause intact —
    /// the daemon's reason for the final attempt is what an operator needs, not the first one's.</para>
    /// </summary>
    private async Task<IAgent> startWithRetriesAsync(Uri agentUri)
    {
        var maxAttempts = Math.Max(1, _runtime.Options.Durability.AgentStartRetryAttempts + 1);

        for (var attempt = 1; ; attempt++)
        {
            var agent = await findAgentAsync(agentUri);
            try
            {
                await agent.StartAsync(_cancellation.Token);
                await _observer.AgentStarted(agentUri);

                if (attempt > 1)
                {
                    _logger.LogInformation(
                        "Successfully started agent {AgentUri} on Node {NodeNumber} on attempt {Attempt}",
                        agentUri, _runtime.Options.Durability.AssignedNodeNumber, attempt);
                }
                else
                {
                    _logger.LogInformation("Successfully started agent {AgentUri} on Node {NodeNumber}", agentUri,
                        _runtime.Options.Durability.AssignedNodeNumber);
                }

                return agent;
            }
            catch (Exception e)
            {
                if (attempt >= maxAttempts || _cancellation.IsCancellationRequested)
                {
                    throw new AgentStartingException(agentUri, _runtime.Options.UniqueNodeId, e);
                }

                var delay = _runtime.Options.Durability.AgentStartRetryDelay * attempt;
                _logger.LogWarning(e,
                    "Attempt {Attempt} of {MaxAttempts} to start agent {AgentUri} on node {NodeNumber} failed; retrying in {Delay}",
                    attempt, maxAttempts, agentUri, _runtime.Options.Durability.AssignedNodeNumber, delay);

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, _cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down mid-retry. Report the start failure the caller was actually
                        // waiting on rather than a cancellation that says nothing about why it failed.
                        throw new AgentStartingException(agentUri, _runtime.Options.UniqueNodeId, e);
                    }
                }
            }
        }
    }

    // Persist that this node owns agentUri. AddAssignmentAsync is an upsert, so this is safe to call for a
    // freshly started agent or an already-running one whose assignment row may have been lost.
    // ensureLocalNodeRegisteredAsync first side-steps FK problems and timing issues (the assignment row
    // references the node row) and, via GH-3604 (D2), re-inserts this node's row with its real identity if a
    // peer deleted it out from under a still-live node.
    private async Task upsertAssignmentAsync(Uri agentUri)
    {
        try
        {
            await ensureLocalNodeRegisteredAsync(_cancellation.Token);
            await _persistence.AddAssignmentAsync(_runtime.Options.UniqueNodeId, agentUri, _cancellation.Token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to persist the assignment of agent {AgentUri} to Node {NodeId}", agentUri,
                _runtime.Options.UniqueNodeId);
        }
    }

    public async Task StopAgentAsync(Uri agentUri)
    {
        if (Agents.TryGetValue(agentUri, out var agent))
        {
            try
            {
                await agent.StopAsync(_cancellation.Token);
                Agents.TryRemove(agentUri, out _);
                _logger.LogInformation("Successfully stopped agent {AgentUri} on node {NodeNumber}", agentUri,
                    _runtime.Options.Durability.AssignedNodeNumber);

                await _observer.AgentStopped(agentUri);
            }
            catch (Exception e)
            {
                throw new AgentStoppingException(agentUri, _runtime.Options.UniqueNodeId, e);
            }
        }

        try
        {
            await _persistence.RemoveAssignmentAsync(_runtime.Options.UniqueNodeId, agentUri, _cancellation.Token);
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error trying to remove the assignment of agent {AgentUri} to Node {NodeId} in persistence", agentUri,
                _runtime.Options.UniqueNodeId);
        }
    }

    public Uri[] AllRunningAgentUris()
    {
        return Agents.Where(x => x.Value.Status != AgentStatus.Stopped).Select(x => x.Key).ToArray();
    }

    /// <summary>
    /// Whether an event-subscription agent's failure is one an automated restart could plausibly clear.
    /// A transient infrastructure fault (<see cref="ShardFailureCategory.Other" />) is exactly what
    /// restart-on-stall exists for; every other category is bound to a specific event or to two processes
    /// racing one shard, and will reproduce identically on the next start. A null failure means the agent
    /// isn't reporting one — the ordinary wedged-shard case GH-3519 recovers — so it stays restartable.
    /// </summary>
    private static bool canSelfHeal(ShardFailure? failure)
        => failure == null || failure.Category == ShardFailureCategory.Other;

    /// <summary>
    /// Surface a locally-owned agent that stopped or paused on a failure: log it with the classified
    /// reason and notify observers, once per transition into the failed state. See GH-3637 / GH-3638.
    /// </summary>
    private async Task reportAgentPausedAsync(Uri agentUri, ShardFailure? failure)
    {
        if (!_reportedFailures.TryAdd(agentUri, 0))
        {
            return;
        }

        _logger.LogError(
            "Agent {AgentUri} on node {NodeNumber} is not running because of a failure it cannot recover from by restarting: {Failure}. It will be left alone until the underlying problem is resolved.{Detail}",
            agentUri, _runtime.Options.Durability.AssignedNodeNumber,
            failure?.ToString() ?? "no reason reported",
            failure == null ? string.Empty : Environment.NewLine + failure.Detail);

        try
        {
            await _observer.AgentPaused(agentUri, failure);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error notifying observers that agent {AgentUri} paused", agentUri);
        }
    }

    /// <summary>
    /// Sweep this node's own agents for any that have stopped or paused underneath us on a reported
    /// failure. Runs on every node on every health-check tick, independently of leadership: the daemon
    /// pauses a shard on the node that owns it, and before this nothing in the assignment plane ever
    /// distinguished a paused shard from a running one — the anti-thrash guards kept it from being
    /// restarted in a loop, so its progress just silently flatlined. See GH-3637 / GH-3638.
    /// </summary>
    internal async Task ReportFailedLocalAgentsAsync()
    {
        foreach (var entry in Agents.ToArray())
        {
            if (entry.Value is not IEventSubscriptionAgent subscription)
            {
                continue;
            }

            // Status is read once: it delegates to the live daemon shard (GH-3519), so two reads in one
            // pass can legitimately disagree.
            var status = subscription.Status;
            if (status == AgentStatus.Running)
            {
                _reportedFailures.TryRemove(entry.Key, out _);
                continue;
            }

            var failure = subscription.Failure;
            if (failure == null)
            {
                // Stopped with nothing to report is the ordinary wedged-shard case; GH-3519's recovery in
                // StartAgentAsync owns that one, and reporting it here would fire on every stop.
                continue;
            }

            await reportAgentPausedAsync(entry.Key, failure);
        }
    }

    /// <summary>
    ///     THIS IS STRICTLY FOR TESTING
    /// </summary>
    internal async Task DisableAgentsAsync()
    {
        var agents = Agents.Select(x => x.Value).ToArray();
        foreach (var agent in agents)
        {
            await agent.StopAsync(CancellationToken.None);
        }

        await _persistence.ReleaseLeadershipLockAsync();

        await _cancellation.CancelAsync();
    }
}

public class AgentStartingException : Exception
{
    public AgentStartingException(Uri agentUri, Guid nodeId, Exception? innerException) : base(
        $"Failed trying to start agent {agentUri} on node {nodeId}", innerException)
    {
    }
}

public class AgentStoppingException : Exception
{
    public AgentStoppingException(Uri agentUri, Guid nodeId, Exception? innerException) : base(
        $"Failed trying to stop agent {agentUri} on node {nodeId}", innerException)
    {
    }
}