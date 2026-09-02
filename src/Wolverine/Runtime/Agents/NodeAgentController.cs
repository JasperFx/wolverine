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

    // GH-3888: agents this node has released after exhausting local auto-restarts, mapped to when this
    // node may advertise their capability again. While an entry is live, buildLocalNode() withholds the
    // agent's URI from the node's advertised capabilities — which is what keeps the leader's
    // capability-matched distribution from handing the agent straight back to the node that just failed
    // it. A ConcurrentDictionary because the independent heartbeat loop reads it (through
    // buildLocalNode on a row-resurrection) while the serialized health-check path mutates it.
    private readonly ConcurrentDictionary<Uri, DateTimeOffset> _releasedAgents = new();

    // GH-3888: agents that exhausted their local restart budget while NO live peer advertised the
    // capability to run them, so the release was declined. Used to log that once per episode rather
    // than on every sweep tick. Only touched from the serialized health-check path.
    private readonly HashSet<Uri> _reportedUnreleasable = new();

    // GH-4193: agents this node has already tried to restart after finding them Stopped with no failure
    // to report. Dropped the moment the agent is seen Running again, exactly like _reportedFailures, so
    // a shard that recovers and later wedges anew gets a fresh attempt while one that cannot stay up
    // does not re-drive a start on every tick. Only touched from the serialized health-check path.
    private readonly HashSet<Uri> _restartedWedgedAgents = new();

    // GH-3970: consecutive failures to BUILD or START an agent here, keyed by agent Uri. A failed build
    // leaves nothing in Agents, so the GH-3888 stall detector -- which sweeps the agents this node is
    // actually running -- structurally cannot see it, and no restart budget is ever consumed. Counting
    // the failures here is what gives that release path something to act on. A successful start removes
    // the entry. A ConcurrentDictionary because starts run with bounded parallelism (StartBatchAsync)
    // while the serialized health-check path reads the counts.
    private readonly ConcurrentDictionary<Uri, FailedStartRecord> _failedStarts = new();

    /// <summary>
    /// GH-3970: how many times in a row this node has failed to build/start one agent, and why it failed
    /// the last time. The exception is kept so the release decision can say what actually went wrong --
    /// for the reported field failure that is an <c>ArgumentOutOfRangeException</c> naming the projection
    /// version this node does not carry, which is exactly the detail an operator needs to see.
    /// </summary>
    internal sealed record FailedStartRecord(int Count, Exception? Failure)
    {
        public FailedStartRecord Next(Exception? failure) => new(Count + 1, failure ?? Failure);
    }

    /// <summary>
    /// GH-3888: clock for the release-embargo bookkeeping. Tests substitute it so the cooldown can be
    /// exercised without waiting it out; everything else uses the system clock.
    /// </summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

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

        if (runtime.Options.Durability.Mode == DurabilityMode.Balanced)
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

        if (runtime.Options.Durability.DurabilityAgentEnabled)
        {
            _agentFamilies[_runtime.Stores.Scheme] = _runtime.Stores;
        }

        foreach (var family in runtime.Options.Transports.OfType<IAgentFamilySource>().SelectMany(x => x.BuildAgentFamilySources(runtime)))
        {
            _agentFamilies[family.Scheme] = family;
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _logger = logger;
    }

    public ConcurrentDictionary<Uri, IAgent> Agents { get; } = new();

    // GH-3748: background executor for remotely-received batched agent commands, created by
    // WolverineRuntime for Balanced-mode nodes. See DeferredAgentCommandRunner.
    private DeferredAgentCommandRunner? _deferredWork;

    internal DeferredAgentCommandRunner? DeferredWork
    {
        get => _deferredWork;
        set => _deferredWork = value;
    }

    /// <summary>
    ///     Claim the deferred command runner for disposal, so that only one of two racing teardown
    ///     passes owns it and the loser gets null rather than a field cleared out from under it.
    /// </summary>
    internal DeferredAgentCommandRunner? TakeDeferredWork() => Interlocked.Exchange(ref _deferredWork, null);

    // GH-3748: the serial control lane used to guarantee that a stop command arriving after a batch of
    // starts also EXECUTED after those starts. With batch execution deferred off the lane, that ordering
    // is kept per agent instead: every stop stamps a monotonic sequence for its agent, and a deferred
    // start skips (or immediately reverts) any agent whose stop is newer than the batch's acceptance.
    private long _commandSequence;
    private readonly ConcurrentDictionary<Uri, long> _stopRevocations = new();

    internal long CurrentCommandSequence => Volatile.Read(ref _commandSequence);

    private bool isRevokedSince(Uri agentUri, long sequence)
        => _stopRevocations.TryGetValue(agentUri, out var revokedAt) && revokedAt > sequence;

    /// <summary>
    ///     GH-3748: start one agent from a deferred batch, honoring any stop command that arrived after
    ///     the batch was accepted. Returns whether the agent is genuinely running here afterward.
    /// </summary>
    internal async Task<bool> StartAgentGuardedAsync(Uri agentUri, long asOfSequence)
    {
        if (isRevokedSince(agentUri, asOfSequence))
        {
            _logger.LogInformation(
                "Skipping deferred start of agent {AgentUri} on node {NodeNumber}: a stop command arrived after the batch was accepted",
                agentUri, _runtime.Options.Durability.AssignedNodeNumber);
            return false;
        }

        await StartAgentAsync(agentUri);

        // A stop that landed while this start was in flight found nothing to stop — its no-op must not
        // stand while the agent it aimed at comes up right behind it.
        if (isRevokedSince(agentUri, asOfSequence))
        {
            _logger.LogInformation(
                "Reverting deferred start of agent {AgentUri} on node {NodeNumber}: a stop command arrived while the start was in flight",
                agentUri, _runtime.Options.Durability.AssignedNodeNumber);
            await StopAgentAsync(agentUri);
            return false;
        }

        return true;
    }

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
        handlers.RegisterMessageType(typeof(QueryAgentPresence));
        handlers.RegisterMessageType(typeof(AgentPresenceReport));
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

        IAgent agent;
        try
        {
            agent = await startWithRetriesAsync(agentUri);
        }
        catch (Exception e)
        {
            // GH-3970: count it before it propagates. The caller (StartBatchAsync) logs and swallows, and
            // the leader is told only that the agent is "unconfirmed" -- which it deliberately does not
            // treat as a failure (GH-3750) -- so without this ledger nothing anywhere distinguishes "this
            // node is still working on it" from "this node threw and will throw again". This node caught
            // the exception; it is the only place that knows.
            _failedStarts.AddOrUpdate(agentUri, _ => new FailedStartRecord(1, e), (_, existing) => existing.Next(e));
            throw;
        }

        Agents[agentUri] = agent;

        // GH-3638: a start that succeeded supersedes whatever failure was last reported for this agent, so
        // a later failure alerts again instead of being swallowed as a duplicate of the old one.
        _reportedFailures.TryRemove(agentUri, out _);

        // GH-3970: likewise for the failed-start budget -- the count is CONSECUTIVE failures, so anything
        // that comes up clears it and a later relapse gets a full budget of its own.
        _failedStarts.TryRemove(agentUri, out _);

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
        // GH-3748: stamped before the attempt, so even a stop that finds nothing running revokes any
        // deferred start still queued or in flight for this agent. See StartAgentGuardedAsync.
        _stopRevocations[agentUri] = Interlocked.Increment(ref _commandSequence);

        // GH-3970: a stop ends this node's run of consecutive failed starts, whatever the reason for it.
        // Without this an agent that failed here, was placed elsewhere, and came back much later would
        // resume an ancient count and be released on its first fresh failure instead of getting the full
        // budget the setting promises.
        _failedStarts.TryRemove(agentUri, out _);

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
    /// GH-4193: re-drive a locally-owned agent found <see cref="AgentStatus.Stopped" /> with no failure
    /// to report. <see cref="StartAgentAsync" /> already knows how to replace a dead registration -- it
    /// evicts, stops, and starts again with retries -- so this only has to call it; the gap was that
    /// nothing ever did.
    /// </summary>
    /// <remarks>
    /// Deliberately <see cref="AgentStatus.Stopped" /> only. A <see cref="AgentStatus.Paused" /> agent
    /// with no failure is one whose owner schedules its own resume -- an error backoff, or the
    /// blue/green side-effect gate -- and restarting it here would fight that owner, which is the thing
    /// the anti-thrash guards in this class exist to prevent.
    /// </remarks>
    private async Task restartWedgedLocalAgentAsync(Uri agentUri, AgentStatus status)
    {
        if (status != AgentStatus.Stopped)
        {
            return;
        }

        // Once per transition into the wedged state, cleared when the agent is next seen Running.
        if (!_restartedWedgedAgents.Add(agentUri))
        {
            return;
        }

        _logger.LogInformation(
            "Agent {AgentUri} on node {NodeNumber} is still assigned here but its shard is stopped with no failure reported; restarting it",
            agentUri, _runtime.Options.Durability.AssignedNodeNumber);

        try
        {
            await StartAgentAsync(agentUri);
        }
        catch (Exception e)
        {
            // A failed start is already counted and escalated by _failedStarts / the release path; the
            // sweep must not be taken down by one agent.
            _logger.LogError(e, "Error restarting wedged agent {AgentUri}", agentUri);
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
        List<ReleaseCandidate>? exhausted = null;

        foreach (var entry in Agents.ToArray())
        {
            if (entry.Value is not IEventSubscriptionAgent subscription)
            {
                continue;
            }

            // GH-3888: an agent that burned through its local auto-restart budget without ever
            // advancing is asking to be placed somewhere else. Checked BEFORE the Running
            // short-circuit below — a stalled shard still reads Running.
            if (subscription.LocalRestartsExhausted)
            {
                (exhausted ??= new()).Add(new ReleaseCandidate(entry.Key, subscription, null));
                continue;
            }

            // Status is read once: it delegates to the live daemon shard (GH-3519), so two reads in one
            // pass can legitimately disagree.
            var status = subscription.Status;
            if (status == AgentStatus.Running)
            {
                _reportedFailures.TryRemove(entry.Key, out _);
                _reportedUnreleasable.Remove(entry.Key);
                _restartedWedgedAgents.Remove(entry.Key);
                continue;
            }

            var failure = subscription.Failure;
            if (failure == null)
            {
                // GH-4193: this used to `continue`, on the grounds that GH-3519's recovery in
                // StartAgentAsync owns the ordinary wedged-shard case. It does not get the chance:
                // StartAgentAsync only runs when the leader emits an assignment command, and
                // AssignmentGrid.Agent.TryBuildAssignmentCommand returns false on
                // `AssignedNode == OriginalNode`. A local stop does not touch the persisted assignment
                // row, so the two stay equal forever and no command is ever built. Both recovery paths
                // deferred to each other and the shard's sequence never moved again -- reachable
                // deliberately through EventStoreAgents.TryRebuildRegisteredProjectionAsync (GH-3163),
                // whose transient rebuild stops the continuous agent and has no resumeContinuousAsync.
                //
                // The old comment's other reason -- "would fire on every stop" -- does not hold for an
                // agent still present in Agents: StopAgentAsync removes it from the dictionary and drops
                // the assignment row, so Stopped-and-still-registered is BY CONSTRUCTION a wedged shard
                // and never a deliberate stop.
                await restartWedgedLocalAgentAsync(entry.Key, status);
                continue;
            }

            await reportAgentPausedAsync(entry.Key, failure);
        }

        // GH-3970: the other half of the sweep. These agents are NOT in Agents -- the start threw before
        // anything could be registered -- so the loop above structurally cannot reach them. They ride the
        // same release policy: a bounded budget, a live capable peer required, and the same capability
        // embargo to stop the leader handing the agent straight back.
        var startBudget = _runtime.Options.Durability.MaxAgentStartFailuresBeforeRelease;
        if (startBudget > 0)
        {
            foreach (var entry in _failedStarts.ToArray())
            {
                if (entry.Value.Count < startBudget) continue;

                // A start that failed and then succeeded on a later tick has already cleared itself out
                // of the ledger, so anything still here has failed every attempt since.
                (exhausted ??= new()).Add(new ReleaseCandidate(entry.Key, null, entry.Value.Failure));
            }
        }

        if (exhausted != null)
        {
            await tryReleaseExhaustedAgentsAsync(exhausted);
        }
    }

    /// <summary>
    /// An agent this node wants to hand back so the leader can place it on a capable peer. Two sources,
    /// deliberately sharing one release policy and one embargo:
    ///
    /// <para><b>GH-3888</b> — a running <see cref="IEventSubscriptionAgent" /> that burned its node-local
    /// auto-restart budget without ever advancing. <see cref="Agent" /> is the live instance.</para>
    ///
    /// <para><b>GH-3970</b> — an agent that could not be built or started here at all, so there is no
    /// instance to carry a budget or report a <c>ShardFailure</c>. <see cref="Agent" /> is null and
    /// <see cref="StartFailure" /> is the last exception the start threw.</para>
    /// </summary>
    internal sealed record ReleaseCandidate(Uri Uri, IEventSubscriptionAgent? Agent, Exception? StartFailure)
    {
        public bool IsFailedStart => Agent is null;
    }

    /// <summary>
    /// GH-3888: release agents whose node-local auto-restart budget is exhausted, so the leader can
    /// place them on a healthy peer. The field failure this exists for: a node in a memory-starved /
    /// GC-death-spiral state keeps writing heartbeats (the heartbeat loop is deliberately cheap and
    /// isolated, GH-3604/D1), so it never looks stale to its peers — and the only recovery its stalled
    /// shards ever get is EventSubscriptionAgent's node-local stop/start, which re-runs the same starved
    /// conditions forever. 53 shards of a shared projection version sat frozen for sixteen minutes next
    /// to a healthy fleet advertising the same capability until a manual pod restart.
    ///
    /// <para>Release only happens when at least one other live node advertises the agent's URI as a
    /// capability — otherwise there is nowhere better to go, and the agent's budget is refunded so local
    /// retries continue rather than freezing the shard. A released agent's URI goes under a capability
    /// embargo (<see cref="DurabilitySettings.AgentReleaseCooldown" />): this node stops advertising it,
    /// which is what stops the leader's capability-matched distribution handing the agent straight back.
    /// The embargo is written to the node row FIRST, then the assignment is dropped, so no evaluation can
    /// observe the freed agent while this node still advertises for it.</para>
    /// </summary>
    private async Task tryReleaseExhaustedAgentsAsync(List<ReleaseCandidate> exhausted)
    {
        IReadOnlyList<WolverineNode> nodes;
        try
        {
            (nodes, _) = await _persistence.LoadNodeAgentStateAsync(_cancellation.Token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error loading node state while evaluating stalled-agent release on node {NodeNumber}",
                _runtime.Options.Durability.AssignedNodeNumber);
            return;
        }

        var staleTime = DateTimeOffset.UtcNow.Subtract(_runtime.Options.Durability.StaleNodeTimeout);
        var selfId = _runtime.Options.UniqueNodeId;
        var peers = nodes
            .Where(x => x.NodeId != selfId && x.LastHealthCheck >= staleTime)
            .ToArray();

        var releasable = new List<ReleaseCandidate>();
        foreach (var pair in exhausted)
        {
            if (peers.Any(p => p.Capabilities.Contains(pair.Uri)))
            {
                releasable.Add(pair);
            }
            else
            {
                // Nowhere better to go: no live peer advertises this agent, so releasing it would
                // strand the shard entirely. Keep the pre-existing local retries — the least-bad
                // option — and say so once per episode rather than on every tick.
                //
                // GH-3970: this is also the branch every agent whose family is NOT an IStaticAgentFamily
                // lands in, permanently and by construction. Only static families contribute to a node's
                // advertised Capabilities, so a wolverinedb:// durability agent can never match a peer
                // here. That is the correct outcome — those agents have no capability-matched alternative
                // node to be released TO — and it means the pre-GH-3970 behaviour of retrying locally is
                // preserved exactly for them.
                if (_reportedUnreleasable.Add(pair.Uri))
                {
                    _logger.LogWarning(
                        "Agent {AgentUri} on node {NodeNumber} exhausted its local {Budget} budget, but no live peer advertises the capability to run it. Local retries continue.",
                        pair.Uri, _runtime.Options.Durability.AssignedNodeNumber,
                        pair.IsFailedStart ? "start" : "auto-restart");
                }

                // Refund the budget so local retries continue rather than freezing here. For a failed
                // start that means clearing the count: the next tick starts a fresh budget, and the
                // release is reconsidered only after another full run of failures.
                if (pair.IsFailedStart)
                {
                    _failedStarts.TryRemove(pair.Uri, out _);
                }
                else
                {
                    pair.Agent!.ResetLocalRestartBudget();
                }
            }
        }

        if (releasable.Count == 0)
        {
            return;
        }

        // Embargo first, then persist the shrunk capability set, and only then let go of the agents:
        // the leader must never observe the released assignment gone while this node's row still
        // advertises the capability, or the very next evaluation hands the agent straight back.
        var embargoUntil = TimeProvider.GetUtcNow().Add(_runtime.Options.Durability.AgentReleaseCooldown);
        foreach (var pair in releasable)
        {
            _releasedAgents[pair.Uri] = embargoUntil;
        }

        try
        {
            await _persistence.ReregisterNodeAsync(buildLocalNode(), _cancellation.Token);
        }
        catch (Exception e)
        {
            // Roll the embargo back: the persisted row still advertises these capabilities, so letting
            // the agents go now would just bounce them back here. Try again on a later sweep tick.
            foreach (var pair in releasable)
            {
                _releasedAgents.TryRemove(pair.Uri, out _);
            }

            _logger.LogError(e,
                "Error persisting the reduced capability set while releasing stalled agents on node {NodeNumber}; release deferred",
                _runtime.Options.Durability.AssignedNodeNumber);
            return;
        }

        foreach (var candidate in releasable)
        {
            var uri = candidate.Uri;
            var failure = candidate.Agent?.Failure;

            if (candidate.IsFailedStart)
            {
                _logger.LogWarning(
                    "Agent {AgentUri} on node {NodeNumber} is being released after failing to start here {Attempts} consecutive times. A live peer advertises the same capability, and the leader will reassign it there. This node will not advertise the capability again before {EmbargoUntil:u}. Last start failure: {Failure}",
                    uri, _runtime.Options.Durability.AssignedNodeNumber,
                    _runtime.Options.Durability.MaxAgentStartFailuresBeforeRelease, embargoUntil,
                    candidate.StartFailure?.ToString() ?? "none reported");
            }
            else
            {
                _logger.LogWarning(
                    "Agent {AgentUri} on node {NodeNumber} is being released after exhausting its local auto-restart budget without advancing. A live peer advertises the same capability, and the leader will reassign it there. This node will not advertise the capability again before {EmbargoUntil:u}. Last reported failure: {Failure}",
                    uri, _runtime.Options.Durability.AssignedNodeNumber, embargoUntil,
                    failure?.ToString() ?? "none reported");
            }

            try
            {
                // Still the right call for a failed start even though nothing is running: StopAgentAsync
                // skips the agent block when Agents has no entry and goes straight to
                // RemoveAssignmentAsync. Dropping that assignment row is the point — the embargo alone
                // stops this node being chosen again, but the row is what lets the leader place the agent
                // somewhere else.
                await StopAgentAsync(uri);
            }
            catch (Exception e)
            {
                // The agent stays registered locally, so the next sweep sees it still exhausted and
                // retries the release; the embargo entry is simply refreshed then.
                _logger.LogError(e, "Error stopping agent {AgentUri} while releasing it from node {NodeNumber}",
                    uri, _runtime.Options.Durability.AssignedNodeNumber);
                continue;
            }

            _reportedFailures.TryRemove(uri, out _);
            _reportedUnreleasable.Remove(uri);
            _failedStarts.TryRemove(uri, out _);

            try
            {
                await _observer.AgentReleased(uri, failure);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error notifying observers that agent {AgentUri} was released", uri);
            }
        }
    }

    /// <summary>
    /// GH-3888: lift release embargoes whose cooldown has lapsed and advertise those capabilities
    /// again. A node that has genuinely recovered (the transient node-level fault passed) becomes an
    /// ordinary candidate; one that is still sick will burn another full local restart budget before it
    /// releases again, so the steady-state worst case is one bounded move per cooldown rather than a
    /// reassignment storm.
    /// </summary>
    internal async Task RestoreExpiredReleaseEmbargoesAsync()
    {
        if (_releasedAgents.IsEmpty)
        {
            return;
        }

        var now = TimeProvider.GetUtcNow();
        var expired = _releasedAgents.Where(x => x.Value <= now).Select(x => x.Key).ToArray();
        if (expired.Length == 0)
        {
            return;
        }

        foreach (var uri in expired)
        {
            _releasedAgents.TryRemove(uri, out _);
        }

        try
        {
            await _persistence.ReregisterNodeAsync(buildLocalNode(), _cancellation.Token);

            _logger.LogInformation(
                "Node {NodeNumber} is advertising {Count} previously released agent capability(ies) again now that the release cooldown has lapsed",
                _runtime.Options.Durability.AssignedNodeNumber, expired.Length);
        }
        catch (Exception e)
        {
            // Put the entries back (already due) so the next tick retries the re-advertisement;
            // otherwise the in-memory state would say "advertising" while the persisted row still
            // carries the shrunk capability set.
            foreach (var uri in expired)
            {
                _releasedAgents.TryAdd(uri, now);
            }

            _logger.LogError(e, "Error re-advertising released agent capabilities on node {NodeNumber}",
                _runtime.Options.Durability.AssignedNodeNumber);
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