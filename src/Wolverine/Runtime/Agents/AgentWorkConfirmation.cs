using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Which direction a batched agent command is trying to move its agents, and therefore what counts
///     as confirmation in an <see cref="AgentPresenceReport" />: an agent <i>running</i> on the node
///     confirms a start, an agent <i>absent</i> from the node confirms a stop.
/// </summary>
internal enum AgentPresenceDirection
{
    Running,
    Gone
}

/// <summary>
///     GH-3748 / GH-3750: waits for a batched agent command's outcome on <b>observed progress</b> instead
///     of a clock.
///
///     <para>The typed reply (<see cref="AgentsStarted" /> / <see cref="AgentsStopped" />) only arrives
///     when the whole batch has been worked through, and the old protocol made its reply window carry two
///     jobs at once: how long the work may take, and how long until the leader declares the node failed.
///     No window survives both — a Marten projection shard replaying behind a version bump was measured
///     at 27s p50 / 215s max per shard, so honest work outran every reasonable window, the leader treated
///     the timeout as failure, and agents still mid-start were re-placed onto other nodes (369 of 902
///     agents started more than once in the GH-3750 field data).</para>
///
///     <para>Here the reply is <b>raced</b> against a poll loop asking the destination which of the
///     requested agents are actually running (<see cref="QueryAgentPresence" />). The reply still wins
///     whenever it arrives — including from a pre-upgrade node that has never heard of the query, whose
///     polls simply go unanswered and whose reply window is unchanged. The polls add the piece the
///     protocol never had: progress. As long as one more agent turns up confirmed within
///     <see cref="DurabilitySettings.AgentProgressStallTimeout" />, the leader keeps waiting, so a slow
///     node that is converging gets unbounded time while a wedged one costs a bounded wait — and every
///     agent the leader is waiting on stays held by the dispatcher and the pending-assignment ledger,
///     which is what stops the re-placement churn.</para>
/// </summary>
internal static class AgentWorkConfirmation
{
    public static Task<Uri[]> AwaitAsync(IWolverineRuntime runtime, NodeDestination destination,
        Uri[] requested, Task<Uri[]> workReply, AgentPresenceDirection direction,
        CancellationToken cancellation)
    {
#pragma warning disable VSTHRD003
        return AwaitCoreAsync(
            async uris =>
            {
                var report = await runtime.Agents.InvokeAsync<AgentPresenceReport>(destination,
                    new QueryAgentPresence(uris));
                return report?.Running ?? [];
            },
            workReply,
            requested,
            direction,
            runtime.Options.Durability.AgentProgressPollInterval,
            runtime.Options.Durability.AgentProgressStallTimeout,
            runtime.Logger,
            cancellation);
#pragma warning restore VSTHRD003
    }

    internal static async Task<Uri[]> AwaitCoreAsync(Func<Uri[], Task<Uri[]>> queryRunning,
        Task<Uri[]> workReply, Uri[] requested, AgentPresenceDirection direction,
        TimeSpan pollInterval, TimeSpan stallTimeout, ILogger logger, CancellationToken cancellation)
    {
        var universe = new HashSet<Uri>(requested);
        var confirmed = new HashSet<Uri>();

        // Progress is measured from the last poll that confirmed something NEW, and the stall clock only
        // runs once the destination has answered at least one poll. A destination that never answers —
        // a pre-GH-3748 node, or one that is gone — leaves the reply's own window as the only bound,
        // exactly the pre-existing behavior.
        var answeredAtLeastOnce = false;
        var lastProgress = DateTimeOffset.UtcNow;

        // Null once the reply has faulted and been consumed: from then on the polls carry the wait alone,
        // bounded by the stall clock.
        var pendingReply = workReply;

        while (true)
        {
            if (pendingReply != null)
            {
                var delay = Task.Delay(pollInterval, cancellation);
#pragma warning disable VSTHRD003
                var finished = await Task.WhenAny(pendingReply, delay);
#pragma warning restore VSTHRD003

                if (finished == pendingReply)
                {
                    try
                    {
                        // The reply is the authoritative completion: the batch has been worked through
                        // and this is exactly what succeeded. Anything a poll confirmed on top of it
                        // stands — "running on the node" cannot be less true than the reply.
#pragma warning disable VSTHRD003
                        var replied = await pendingReply;
#pragma warning restore VSTHRD003
                        foreach (var uri in replied)
                        {
                            if (universe.Contains(uri))
                            {
                                confirmed.Add(uri);
                            }
                        }

                        return confirmed.ToArray();
                    }
                    catch (Exception failure)
                    {
                        // The reply window elapsing — or the request faulting outright — is only the end
                        // of the story for a destination whose polls go unanswered. One that is answering
                        // is demonstrably alive and mid-batch, so the wait continues on the polls alone,
                        // bounded by the stall clock instead of the reply window that slow-but-honest
                        // work just outran. That double duty — work budget AND failure detector in one
                        // number — is exactly what GH-3748 reported as impossible to configure.
                        if (!answeredAtLeastOnce)
                        {
                            logger.LogDebug(failure,
                                "Batched agent command reply was not received and the destination is not answering presence polls; proceeding with {ConfirmedCount} of {RequestedCount} agents confirmed",
                                confirmed.Count, universe.Count);
                            return confirmed.ToArray();
                        }

                        logger.LogDebug(failure,
                            "Batched agent command reply was not received; continuing on presence polling with {ConfirmedCount} of {RequestedCount} agents confirmed so far",
                            confirmed.Count, universe.Count);
                        pendingReply = null;
                    }
                }
            }
            else
            {
                try
                {
                    await Task.Delay(pollInterval, cancellation);
                }
                catch (OperationCanceledException)
                {
                    return confirmed.ToArray();
                }
            }

            if (cancellation.IsCancellationRequested)
            {
                observe(workReply);
                return confirmed.ToArray();
            }

            try
            {
                var running = await queryRunning(requested);
                answeredAtLeastOnce = true;

                var confirming = direction == AgentPresenceDirection.Running
                    ? (IEnumerable<Uri>)running
                    : universe.Except(running);

                var progressed = false;
                foreach (var uri in confirming)
                {
                    if (universe.Contains(uri) && confirmed.Add(uri))
                    {
                        progressed = true;
                    }
                }

                if (progressed)
                {
                    lastProgress = DateTimeOffset.UtcNow;
                }

                if (confirmed.Count == universe.Count)
                {
                    // Fully confirmed by observation; no need to hold the lane for the formal reply.
                    observe(workReply);
                    return confirmed.ToArray();
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                observe(workReply);
                return confirmed.ToArray();
            }
            catch (Exception e)
            {
                // An unanswered poll proves nothing either way — the node may predate the query type, be
                // briefly unreachable, or be gone. The reply window remains the bound for those.
                logger.LogDebug(e, "Agent presence poll went unanswered");
            }

            if (answeredAtLeastOnce && DateTimeOffset.UtcNow - lastProgress >= stallTimeout)
            {
                logger.LogWarning(
                    "Giving up on a batched agent command after no progress for {StallTimeout}: {ConfirmedCount} of {RequestedCount} agents confirmed",
                    stallTimeout, confirmed.Count, universe.Count);
                observe(workReply);
                return confirmed.ToArray();
            }
        }
    }

    // A reply abandoned in favor of poll-observed confirmation may still fault later (most likely its
    // own TimeoutException). Observe it so it can't surface as an UnobservedTaskException.
    private static void observe(Task task)
    {
        _ = task.ContinueWith(t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
