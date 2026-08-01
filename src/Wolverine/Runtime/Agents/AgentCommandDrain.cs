using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Drains a wave of agent commands emitted by the leader, one command at a time <b>per destination
///     node</b> while letting different destinations proceed concurrently.
///
///     <para>GH-3698: this drain used to be strictly serial — one command at a time across the whole
///     cluster. Because <c>NodeAgentController.batchCommands</c> groups a wave's <c>AssignAgent</c> commands
///     by destination and appends each destination's chunks contiguously, that made the drain
///     destination-at-a-time: every chunk for node A was awaited to completion before node B was sent its
///     first. Cluster-wide agent start concurrency was therefore capped at a single node's
///     <c>MaxAgentStartParallelism</c> <i>no matter how many nodes were in the cluster</i>, so adding nodes
///     did nothing for convergence time. On a cluster with thousands of slow-starting Marten subscription
///     agents that is hours of one node working while its peers sit idle — and because
///     <c>WolverineRuntime.executeHealthChecks</c> awaits the drain before the next
///     <c>EvaluateAssignmentsAsync</c>, the leader also stopped writing <c>AssignmentChanged</c> records for
///     the whole duration, which is why the reported cluster looked stalled rather than merely slow.</para>
/// </summary>
internal class AgentCommandDrain
{
    private readonly Func<IAgentCommand, CancellationToken, Task<AgentCommands?>> _executor;
    private readonly ILogger _logger;

    internal AgentCommandDrain(Func<IAgentCommand, CancellationToken, Task<AgentCommands?>> executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    ///     Drains every command in the wave, and returns whatever failed along the way rather than throwing.
    ///     A failure must never cost the rest of the wave (see the catch below), but callers that answer to
    ///     an operator — <c>ApplyRestrictionsAsync</c> — still need to know, so the decision of what to do
    ///     about a failure belongs to them and not here.
    /// </summary>
    public async Task<IReadOnlyList<Exception>> DrainAsync(AgentCommands commands,
        CancellationToken cancellationToken)
    {
        var failures = new ConcurrentBag<Exception>();

        while (commands.Count != 0)
        {
            // Cascaded commands are deferred to the next round rather than appended to the lane that
            // produced them. That keeps the "a cascade runs after its producer" ordering the serial drain
            // had, and re-partitions them against the destinations of the round they actually belong to --
            // e.g. ReassignAgent runs in the source node's lane (GH-3749) and cascades an AssignAgent that
            // is keyed onto the destination's lane next round.
            var round = commands.ToArray();
            commands.Clear();

            // Lane key: the destination node, or Guid.Empty for the shared serial lane that holds every
            // command naming no single destination. Ordering WITHIN a lane is preserved; ordering ACROSS
            // lanes deliberately is not. That is safe for what the leader emits: a stop and a start for the
            // same agent on the same node land in the same lane and so stay ordered, and the only
            // cross-node pairing the leader produces for one agent -- a split-brain StopRemoteAgent against
            // the older copy alongside a reassignment away from the newer one -- targets two different
            // nodes with two idempotent stops, and defers its start to the next round.
            var lanes = round.GroupBy(x => x.DestinationNodeId ?? Guid.Empty).ToArray();

            var cascaded = new ConcurrentBag<IAgentCommand>();

            // The lane count is bounded by the size of the cluster, so no explicit degree-of-parallelism cap
            // is needed here -- the actual work is bounded on each receiving node by MaxAgentStartParallelism.
            await Parallel.ForEachAsync(lanes, cancellationToken, async (lane, token) =>
            {
                foreach (var command in lane)
                {
                    try
                    {
                        var additional = await _executor(command, token);
                        if (additional != null)
                        {
                            foreach (var next in additional) cascaded.Add(next);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        // GH-3698: isolate the failure to this one command. Previously any exception -- most
                        // realistically a TimeoutException from an AssignAgents chunk that outran its reply
                        // window -- unwound all the way out of the drain and DISCARDED every command still
                        // queued for every other node, so one slow node could void an entire assignment wave.
                        _logger.LogError(e,
                            "Error trying to execute agent command {Command} against node {NodeId}",
                            command, command.DestinationNodeId);
                        failures.Add(e);
                    }
                }
            });

            commands.AddRange(cascaded);
        }

        return failures.ToArray();
    }
}
