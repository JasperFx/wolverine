using System.Collections.Concurrent;
using ImTools;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime;

public class UnknownWolverineNodeException : Exception
{
    public UnknownWolverineNodeException(Guid nodeId) : base(
        $"Attempting to make a remote call to an unknown node {nodeId}. This could be from that node having gone offline before the message was processed.")
    {
    }
}

public partial class WolverineRuntime : IAgentRuntime
{
    private bool _agentsAreDisabled;
    private Task? _healthCheckLoop;
    private Task? _heartbeatLoop;

    // GH-3698: executes the commands the health-check/assignment loop produces, on a persistent lane per
    // destination node, so neither a slow node nor a dead one can stop the leader from re-evaluating
    // assignments or hold up the other nodes. See AgentCommandDispatcher.
    private AgentCommandDispatcher? _dispatcher;

    private readonly CancellationTokenSource _agentCancellation;

    public NodeAgentController? NodeController { get; private set; }

    public Task StartLocallyAsync(Uri agentUri)
    {
        if (Cancellation.IsCancellationRequested || _agentsAreDisabled) return Task.CompletedTask;

        if (NodeController == null)
        {
            throw new InvalidOperationException("This WolverineRuntime does not support stateful agents");
        }

        return NodeController.StartAgentAsync(agentUri);
    }

    public Task StopLocallyAsync(Uri agentUri)
    {
        if (Cancellation.IsCancellationRequested || _agentsAreDisabled) return Task.CompletedTask;

        if (NodeController == null)
        {
            throw new InvalidOperationException("This WolverineRuntime does not support stateful agents");
        }

        return NodeController.StopAgentAsync(agentUri);
    }

    public async Task InvokeAsync(NodeDestination destination, IAgentCommand command)
    {
        var messageBus = new MessageBus(this);
        if (Options.UniqueNodeId == destination.NodeId)
        {
            await messageBus
                .InvokeAsync(command, _agentCancellation.Token);
        }
        else
        {
            await messageBus
                .EndpointFor(destination.ControlUri)
                .InvokeAsync(command, _agentCancellation.Token, 60.Seconds());
        }
    }

    public async Task<T> InvokeAsync<T>(NodeDestination destination, IAgentCommand command, TimeSpan? timeout = null) where T : class
    {
        var messageBus = new MessageBus(this);

        // GH-3604 / D3: callers that dispatch large agent batches (AssignAgents) pass a timeout scaled to the
        // batch size so a big, slow chunk isn't cut off by the fixed default reply window.
        var replyTimeout = timeout ?? 30.Seconds();

        if (Options.UniqueNodeId == destination.NodeId)
        {
            return await messageBus.InvokeAsync<T>(command, _agentCancellation.Token, replyTimeout);
        }

        // GH-2949: remote-node InvokeAsync<T> used to wait only 10s for the typed reply while
        // the same-node path above gets 30s. The asymmetry is backwards - a remote
        // request-reply traverses the control endpoint + serialization + network, so it needs
        // at least as much budget as a same-node in-memory call, not less. The 10s ceiling was
        // tight enough that loaded CI runners (especially the RavenDb leader-election tests
        // that chain `StartAgents` -> `AgentsStarted` during leadership takeover) hit it on a
        // real timing race and surfaced 'Timed out waiting for expected response
        // Wolverine.Runtime.Agents.AgentsStarted ... configured timeout of 10000 milliseconds'.
        return await messageBus
            .EndpointFor(destination.ControlUri)
            .InvokeAsync<T>(command, _agentCancellation.Token, replyTimeout);
    }

    public Uri[] AllRunningAgentUris()
    {
        return NodeController?.AllRunningAgentUris() ?? Array.Empty<Uri>();
    }

    public IReadOnlyList<DatabaseId> AllLocallyOwnedDatabaseIds()
    {
        return AllRunningAgentUris()
            .Select(EventSubscriptionAgentFamily.DatabaseIdOf)
            .Where(id => id != null)
            .Select(id => id!)
            .Distinct()
            .ToList();
    }

    public bool IsLeader()
    {
        if (Options.Durability.Mode == DurabilityMode.Balanced && NodeController != null)
            return NodeController.IsLeader;
        return false;
    }

    public async Task KickstartHealthDetectionAsync()
    {
        if (NodeController != null)
        {
            await new MessageBus(this).InvokeAsync(new CheckAgentHealth(), Options.Durability.Cancellation);
        }
    }

    public Task<AgentCommands> DoHealthChecksAsync()
    {
        if (NodeController != null) return NodeController.DoHealthChecksAsync();
        return Task.FromResult(AgentCommands.Empty);
    }

    public void DisableHealthChecks()
    {
        _agentCancellation.Cancel();

        if (_healthCheckLoop == null) return;

        NodeController?.CancelHeartbeatChecking();
    }

    public async Task ApplyRestrictionsAsync(AgentRestrictions restrictions, CancellationToken cancellationToken)
    {
        // GH-3666: merge and persist the operator's restriction change BEFORE kickstarting health
        // detection. The kickstart runs a full leader assignment evaluation against the restrictions
        // currently in the database, so with the old order (kickstart first) that evaluation acted
        // against the operator's intent — for a pause it could briefly START the very agent being
        // paused (observed in the GH-3663 repro, where it also armed a pending-assignment ledger entry
        // for the doomed start), only for the merged evaluation below to stop it one beat later. With
        // the persist first, every evaluation that runs — the kickstart's included — sees the change.
        var (nodes, assignments) = await Storage.Nodes.LoadNodeAgentStateAsync(cancellationToken);
        assignments.MergeChanges(restrictions);
        await Storage.Nodes.PersistAgentRestrictionsAsync(assignments.FindChanges(), cancellationToken);

        // Still worth doing before the explicit evaluation below: refreshes this node's heartbeat and,
        // on the leader, settles leadership/ejection state so the evaluation runs against a live grid.
        // The `nodes` snapshot above predating this heartbeat is fine — EvaluateAssignmentsAsync already
        // injects the current node if a stale snapshot omitted it (GH-2682).
        await KickstartHealthDetectionAsync();

        // GH-3396 / CritterWatch GH-698: EvaluateAssignmentsAsync *returns* the commands that carry
        // the restriction out to the cluster — pausing an agent detaches it from the grid, which
        // produces a StopRemoteAgent. Discarding that return value meant the pause was persisted and
        // then had NO immediate effect: the agent kept running until some later leader heartbeat
        // happened to re-read restrictions from the database. Every other caller of
        // EvaluateAssignmentsAsync pumps these commands; so does this one now.
        var commands = await NodeController!.EvaluateAssignmentsAsync(nodes, assignments);
        var failures = await executeAgentCommandsAsync(commands, cancellationToken);

        // The in-memory copy is otherwise only ever loaded at startup, but ListeningAgent reads it
        // on every start attempt to decide whether a listener is meant to stay paused — so without
        // this a listener paused through restrictions would keep restarting until the node did.
        Restrictions = assignments;

        // GH-3698: the drain no longer lets one failed command discard the rest of the wave, but this is an
        // operator-initiated call — "your pause was applied" must not be the answer when the StopRemoteAgent
        // carrying it out failed. Every other command still ran, and the restriction is persisted either way.
        if (failures.Count != 0)
        {
            throw new AggregateException(
                "One or more agent commands carrying this restriction change failed. The restriction itself was persisted.",
                failures);
        }
    }

    // Drains an AgentCommands cascade through the message bus. Commands can yield further commands,
    // so this keeps pumping until the queue is empty. The per-destination fan-out that makes this scale
    // with cluster size — and the reason it had to change — lives in AgentCommandDrain (GH-3698).
    private Task<IReadOnlyList<Exception>> executeAgentCommandsAsync(AgentCommands commands,
        CancellationToken cancellationToken)
    {
        return buildDrain().DrainAsync(commands, cancellationToken);
    }

    private AgentCommandDrain buildDrain()
    {
        // A MessageBus is not built to be shared across concurrent invocations, and the drain runs one lane
        // per destination node at the same time, so each command gets its own.
        return new AgentCommandDrain(
            async (command, token) => await new MessageBus(this).InvokeAsync<AgentCommands>(command, token),
            Logger);
    }

    public bool TryFindActiveAgent<T>(Uri agentUri, out T agent) where T : class
    {
        agent = default!;
        if (NodeController!.Agents.TryGetValue(agentUri, out var raw))
        {
            agent = (raw as T)!;
            return agent != null;
        }

        return false;
    }

    public IAgentRuntime Agents => this;

    private async Task startAgentsAsync()
    {
        if (Storage is NullMessageStore)
        {
            // A Solo host is always logically node 1, even with no durable store to coordinate
            // through. StartSoloModeAsync() — the only place a stored Solo node gets its number —
            // sits past this early return, so without this a storeless Solo host keeps its random
            // per-process default and leaks a churning id into heartbeats / envelope ownership.
            // Identity must be set here, before the messaging transports start; the matching
            // NodeStarted()/NodeStopped() lifecycle bookends are owned by SoloHeartbeatService so
            // they fire while transports are up. See #3188.
            if (Options.Durability.Mode == DurabilityMode.Solo)
            {
                Options.Durability.AssignedNodeNumber = 1;
            }

            // With no message store there is no node coordination for the recurring-message agent
            // to ride — but no cluster to coordinate either, so "single agent per cluster" is
            // satisfied by starting it directly. Recurring messages then run on the in-memory
            // scheduled model: the schedule itself survives a restart (the agent re-establishes
            // the next occurrence from the registrations), but an occurrence inside the restart
            // window is lost and nothing dedupes — the startup warning in HostService names both.
            if (Options.Schedules.Any())
            {
                InMemoryRecurringAgent = _container.GetAllInstances<IAgentFamily>()
                    .OfType<Recurring.RecurringMessageAgent>().FirstOrDefault();

                if (InMemoryRecurringAgent != null)
                {
                    await ((IHostedService)InMemoryRecurringAgent).StartAsync(_agentCancellation.Token);
                }
            }

            return;
        }

        switch (Options.Durability.Mode)
        {
            case DurabilityMode.Balanced:
                await startDurableScheduledJobs();
                startNodeAgentController();
                break;


            case DurabilityMode.Solo:
                await startDurableScheduledJobs();
                startNodeAgentController();
                await NodeController!.StartSoloModeAsync();
                break;


            case DurabilityMode.Serverless:
            case DurabilityMode.MediatorOnly:
                break;
        }
    }

    private async Task startDurableScheduledJobs()
    {
        DurableScheduledJobs = await _stores.Value.StartScheduledJobProcessing(this);
    }

    internal IAgent? DurableScheduledJobs { get; private set; }

    /// <summary>
    /// Set only on a storeless host, where the recurring-message agent is started directly rather
    /// than through node coordination. See the note in <c>startAgentsAsync</c>.
    /// </summary>
    internal IAgent? InMemoryRecurringAgent { get; private set; }

    private void startNodeAgentController()
    {
        INodeAgentPersistence nodePersistence = Options.Durability.Mode == DurabilityMode.Balanced
            ? Storage.Nodes
            : new NullNodeAgentPersistence();

        NodeController = new NodeAgentController(this, nodePersistence, _container.GetAllInstances<IAgentFamily>(),
            LoggerFactory.CreateLogger<NodeAgentController>(), _agentCancellation.Token);

        NodeController.AddHandlers(this);

        // GH-3748: only a Balanced node receives batched agent commands over the wire; Solo executes
        // everything locally and inline.
        if (Options.Durability.Mode == DurabilityMode.Balanced)
        {
            NodeController.DeferredWork = new DeferredAgentCommandRunner(this, NodeController, _agentCancellation.Token);
        }
    }

    /// <summary>
    ///     GH-3748: send the typed reply for an agent command whose execution was deferred off the
    ///     control lane, correlated to the original request so the requester's pending
    ///     <c>InvokeAsync&lt;T&gt;</c> completes exactly as if the command had run inline. See
    ///     <see cref="DeferredAgentCommandRunner" />.
    /// </summary>
    internal async Task SendAgentCommandReplyAsync(Uri replyUri, Guid conversationId, object reply)
    {
        var envelope = RoutingFor(reply.GetType()).RouteToDestination(reply, replyUri, null);

        // What ReplyTracker.Complete matches on — see MessageContext.TrackEnvelopeCorrelation, which
        // stamps the same thing for an inline cascaded reply.
        envelope.ConversationId = conversationId;
        envelope.IsResponse = true;
        envelope.Source = Options.ServiceName;

        await envelope.StoreAndForwardAsync();
    }

    private async Task startNodeAgentWorkflowAsync()
    {
        // GH-3698: commands execute on the dispatcher's per-destination lanes, independently of the loop
        // that produces them, so a long wave of slow agent starts can never stop assignments being
        // re-evaluated and a lane wedged on a dead node cannot block a healthy one. Built before the loops
        // start so the very first health check has somewhere to put its commands.
        // GH-3781: the AGENT cancellation, not the runtime-wide one -- matching NodeAgentController and
        // DeferredAgentCommandRunner above. StopAsync cancels _agentCancellation first and only calls
        // DurabilitySettings.Cancel() after teardownAgentsAsync has returned, so a dispatcher holding
        // Cancellation had a live token at exactly the moment teardown was awaiting its lanes: nothing
        // unwedged a lane mid-command against a peer that had already gone, and IHost.StopAsync sat there
        // for a full agent-batch reply window per queued command.
        _dispatcher = new AgentCommandDispatcher(
            async (command, token) => await new MessageBus(this).InvokeAsync<AgentCommands>(command, token),
            Logger, _agentCancellation.Token);

        if (NodeController != null)
        {
            // The dispatcher is the leader's "confirmed or failed" signal for a dispatched agent start; see
            // NodeAgentController.PendingDispatches.
            NodeController.PendingDispatches = _dispatcher.TryFindPendingDestination;

            var commands = await NodeController.StartLocalAgentProcessingAsync(Options);
            Replies.AssignedNodeNumber = Options.Durability.AssignedNodeNumber;
            
            foreach (var command in commands)
            {
                await new MessageBus(this).PublishAsync(command);
            }
        }

        // GH-3604 (D1): the heartbeat runs on its OWN loop, independent of executeHealthChecks. See
        // writeHeartbeats. Start it first so a slow first assignment evaluation can't delay the very
        // first heartbeat either.
        _heartbeatLoop = Task.Run(writeHeartbeats, Cancellation);
        _healthCheckLoop = Task.Run(executeHealthChecks, Cancellation);
    }

    // GH-3604 (D1): keep the node heartbeat wholly independent of DoHealthChecksAsync and the agent-command
    // drain in executeHealthChecks. Those two run serially in the same loop, so a leader that spends 60s
    // burning remote InvokeAsync<AgentsStarted> reply timeouts while starting thousands of subscription
    // agents used to delay its own next heartbeat past StaleNodeTimeout — looking dead to its peers
    // precisely when it was doing the most work, getting ejected, and resurrecting under a fresh node
    // number. This loop only ever does the cheap heartbeat write, so no amount of command work can starve
    // it.
    private async Task writeHeartbeats()
    {
        await Task.Delay(Options.Durability.FirstHealthCheckExecution, Cancellation);

        while (!Cancellation.IsCancellationRequested)
        {
            if (NodeController != null)
            {
                try
                {
                    await NodeController.WriteHeartbeatAsync();
                }
                catch (OperationCanceledException)
                {
                    // Nothing here
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Error trying to write the node heartbeat");
                }
            }

            await Task.Delay(Options.Durability.HealthCheckPollingTime, Cancellation);
        }
    }

    // GH-3698 (part 2): the health-check loop no longer executes the commands it produces. It used to await
    // DoHealthChecksAsync() and then the whole agent-command drain in the same loop body, so for as long as
    // a wave of agent starts was in flight, EvaluateAssignmentsAsync did not run at all -- and since the
    // observer early-returns on an empty command list, the leader wrote ZERO AssignmentChanged records the
    // entire time. That is why the reported cluster looked stalled and idle rather than merely slow: the
    // assignments still landing were the ones the current, still-running wave was delivering node by node,
    // while the evaluation that would have produced telemetry (and noticed a topology change) was parked
    // behind it. Same defect as GH-3604/D1, which decoupled the heartbeat; this decouples the other half.
    //
    // Commands are now handed to an independent dispatcher (AgentCommandDispatcher) and this loop moves
    // straight on to its next tick.
    private async Task executeHealthChecks()
    {
        await Task.Delay(Options.Durability.FirstHealthCheckExecution, Cancellation);

        while (!Cancellation.IsCancellationRequested)
        {
            await Task.Delay(Options.Durability.HealthCheckPollingTime, Cancellation);

            if (NodeController != null)
            {
                try
                {
                    var commands = await NodeController.DoHealthChecksAsync();

                    foreach (var command in commands)
                    {
                        _dispatcher?.Enqueue(command);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Nothing here
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Error trying to perform agent health checks");
                }
            }
        }
    }

    private async Task teardownAgentsAsync()
    {
        _heartbeatLoop?.SafeDispose();
        _healthCheckLoop?.SafeDispose();

        // Take each of these once. Re-reading a field after its own null check is what made a
        // redundant teardown pass throw NullReferenceException instead of no-opping: this method
        // clears every field it disposes, so the second reader saw the check pass and the
        // dereference fail. StopAsync latches earlier now, and this no longer depends on it.
        var dispatcher = Interlocked.Exchange(ref _dispatcher, null);
        if (dispatcher != null)
        {
            await dispatcher.DisposeAsync();
        }

        var controller = NodeController;
        if (controller == null)
        {
            return;
        }

        controller.PendingDispatches = null;

        var deferredWork = controller.TakeDeferredWork();
        if (deferredWork != null)
        {
            // Safe to await: _agentCancellation is cancelled before teardown, so an in-flight batch
            // lets go at its next cancellation check rather than running to completion.
            await deferredWork.DisposeAsync();
        }

        var bus = new MessageBus(this);
        await controller.StopAsync(bus);
    }

    /// <summary>
    ///     STRICTLY FOR TESTING!!!!
    /// </summary>
    /// <param name="lastHeartbeatTime"></param>
    internal async Task DisableAgentsAsync(DateTimeOffset lastHeartbeatTime)
    {
        _agentsAreDisabled = true;
        _heartbeatLoop?.SafeDispose();
        _healthCheckLoop?.SafeDispose();

        // Same single-read discipline as teardownAgentsAsync; a test can run this concurrently with
        // a host shutdown, and this method additionally clears NodeController itself.
        var dispatcher = Interlocked.Exchange(ref _dispatcher, null);
        if (dispatcher != null)
        {
            await dispatcher.DisposeAsync();
        }

        var controller = NodeController;
        NodeController = null;
        if (controller == null)
        {
            return;
        }

        controller.PendingDispatches = null;

        var deferredWork = controller.TakeDeferredWork();
        if (deferredWork != null)
        {
            await deferredWork.DisposeAsync();
        }

        await controller.DisableAgentsAsync();
        await Storage.Nodes.OverwriteHealthCheckTimeAsync(Options.UniqueNodeId, lastHeartbeatTime);
    }

    public Task<AgentCommands> StartLocalAgentProcessingAsync()
    {
        if (NodeController != null)
        {
            return NodeController.StartLocalAgentProcessingAsync(Options);
        }

        return Task.FromResult(AgentCommands.Empty);
    }
}