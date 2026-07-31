using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Value semantics for the <c>Uri[]</c> payload every batched agent command carries.
///
///     <para>GH-3698: the record default compares those arrays by <b>reference</b>, so two commands naming
///     exactly the same agents were never equal — which is why <c>StartAgents</c> and <c>StopAgents</c> had
///     hand-written <c>SequenceEqual</c> overrides in the first place. Those overrides were themselves
///     broken: each left <c>GetHashCode</c> returning the array's reference hash, and a hash that disagrees
///     with equality means the two values never land in the same bucket and are never compared at all.</para>
///
///     <para>Comparison is by the actual <see cref="Uri" /> values and is deliberately <b>order-independent</b>.
///     Nothing about a batch depends on the order its URIs happen to be in, and two assignment waves can
///     easily chunk the same set of agents in a different order — treating those as different commands would
///     silently defeat every caller that relies on equality to recognise the same work twice.</para>
/// </summary>
internal static class AgentUriSet
{
    // GH-3750: which of `requested` the other node did NOT confirm. Multiset semantics, matching
    // AreEquivalent — a URI requested twice and confirmed once is still one short.
    public static Uri[] Missing(Uri[] requested, Uri[] confirmed)
    {
        if (requested.Length == 0) return [];

        var counts = new Dictionary<Uri, int>(confirmed.Length);
        foreach (var uri in confirmed)
        {
            counts.TryGetValue(uri, out var count);
            counts[uri] = count + 1;
        }

        var missing = new List<Uri>();
        foreach (var uri in requested)
        {
            if (counts.TryGetValue(uri, out var count) && count > 0)
            {
                counts[uri] = count - 1;
            }
            else
            {
                missing.Add(uri);
            }
        }

        return missing.ToArray();
    }

    public static bool AreEquivalent(Uri[]? left, Uri[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left.Length != right.Length) return false;

        // Multiset, not set: a repeated URI has to be matched by the same number of repeats on the other
        // side rather than collapsing away.
        var counts = new Dictionary<Uri, int>(left.Length);
        foreach (var uri in left)
        {
            counts.TryGetValue(uri, out var count);
            counts[uri] = count + 1;
        }

        foreach (var uri in right)
        {
            if (!counts.TryGetValue(uri, out var count) || count == 0) return false;
            counts[uri] = count - 1;
        }

        return true;
    }

    public static int HashOf(Uri[]? uris)
    {
        if (uris is null) return 0;

        // Summed rather than order-sensitively combined, so any ordering of the same agents hashes alike.
        // Deliberately not XOR: that cancels a duplicated pair back out to zero.
        unchecked
        {
            var hash = uris.Length;
            foreach (var uri in uris) hash += uri.GetHashCode();
            return hash;
        }
    }
}

/// <summary>
///     GH-3733: the wire format for the <c>Uri[]</c> payload every batched agent command carries.
///
///     <para>These commands used to join and split on a comma. A comma is a <b>legal</b> URI character —
///     RFC 3986 lists it as a sub-delim, permitted unescaped in a path segment — and agent URIs embed
///     operator- and tenant-supplied strings: an event-subscription URI is
///     <c>event-subscriptions://marten/main/&lt;host&gt;.&lt;db&gt;/&lt;projection&gt;/all/v&lt;n&gt;/&lt;tenant&gt;</c>.
///     One comma anywhere in one agent URI shattered that agent into fragments, and because the read side
///     built the whole array in a single projection, the resulting <c>UriFormatException</c> took out the
///     <b>entire batch</b>, not just the offending entry. For <c>AgentsStarted</c> — the reply that confirms
///     an agent start — that means the leader gets no confirmation for the whole chunk, which is exactly the
///     shape of stall reported in GH-3698.</para>
///
///     <para>A newline is the delimiter instead: it is not legal unescaped in a URI, and
///     <see cref="Uri.ToString" /> will never emit a bare one.</para>
///
///     <para>The comma nonetheless stays the default on the wire, because it is what every 6.24.0-and-earlier
///     node both writes and reads, and a rolling upgrade has to keep working in both directions. Only a
///     payload that actually contains a comma — the case that was already broken — switches to the newline
///     form, and it announces itself with a leading newline. The two formats can never be confused: a legacy
///     payload always begins with a URI scheme character.</para>
/// </summary>
internal static class AgentUriList
{
    public static byte[] Write(Uri[] uris)
    {
        var texts = uris.Select(x => x.ToString()).ToArray();

        if (texts.Any(x => x.Contains(',')))
        {
            return Encoding.UTF8.GetBytes("\n" + texts.Join("\n"));
        }

        return Encoding.UTF8.GetBytes(texts.Join(","));
    }

    public static Uri[] Read(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var delimiter = text.StartsWith('\n') ? '\n' : ',';

        var parts = text.Split(delimiter, StringSplitOptions.RemoveEmptyEntries);
        var uris = new Uri[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            // Parsed one at a time so a bad entry names itself. The bare
            // "Invalid URI: The format of the URI could not be determined." from a LINQ projection over the
            // whole payload gave no way to tell which agent, or which node, was responsible.
            if (!Uri.TryCreate(parts[i], UriKind.Absolute, out var uri))
            {
                throw new FormatException(
                    $"Unable to read agent Uri '{parts[i]}' out of an agent command payload. Full payload was '{text}'.");
            }

            uris[i] = uri;
        }

        return uris;
    }
}

internal record AgentsStarted(Uri[] AgentUris) : IAgentCommand, ISerializable
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentCommands.Empty);
    }

    public virtual bool Equals(AgentsStarted? other)
        => other is not null && AgentUriSet.AreEquivalent(AgentUris, other.AgentUris);

    public override int GetHashCode() => AgentUriSet.HashOf(AgentUris);

    public byte[] Write()
    {
        return AgentUriList.Write(AgentUris);
    }

    public static object Read(byte[] bytes)
    {
        return new AgentsStarted(AgentUriList.Read(bytes));
    }
}

internal record AssignAgents(NodeDestination Destination, Uri[] AgentIds) : IAgentCommand
{
    public Guid? DestinationNodeId => Destination.NodeId;

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        var startAgents = new StartAgents(AgentIds);

        // GH-3604 / D3, rescaled by GH-3748: scale the reply timeout with the chunk size as a backstop. The
        // receiving node starts the batch with bounded parallelism, so the reply normally arrives quickly,
        // but a large chunk of slow daemon-agent starts must not be cut off by the reply window. See
        // AgentBatchTimeouts for why big chunks get a much larger per-agent allowance.
        var timeout = AgentBatchTimeouts.ReplyWindowFor(AgentIds.Length);
        var response = await runtime.Agents.InvokeAsync<AgentsStarted>(Destination, startAgents, timeout);

        if (response == null)
        {
            return AgentCommands.Empty;
        }

        // GH-3750: the reply is the set of agents that actually reached a started state — StartAgents only
        // bags an agent once StartLocallyAsync has returned — so a PARTIAL reply is the normal case whenever
        // starts are slow (the blue/green side-effect gate's warm-up replay is the field example, measured at
        // 27s p50 / 82s p95 per shard). This used to log the REQUESTED set as "Successfully started"
        // unconditionally, so a chunk that half-started was indistinguishable in the logs from one that fully
        // started, and the operator's only clue was the assignment never converging.
        var confirmed = response.AgentUris ?? [];
        var unconfirmed = AgentUriSet.Missing(AgentIds, confirmed);

        if (unconfirmed.Length == 0)
        {
            runtime.Logger.LogInformation("Successfully started agents {Agents} on node {NodeNumber}", AgentIds, runtime.Options.Durability.AssignedNodeNumber);
        }
        else
        {
            runtime.Logger.LogWarning(
                "Node {NodeNumber} confirmed {ConfirmedCount} of {RequestedCount} requested agents; {UnconfirmedCount} did not report started within {Timeout} and remain unconfirmed: {UnconfirmedAgents}",
                runtime.Options.Durability.AssignedNodeNumber, confirmed.Length, AgentIds.Length,
                unconfirmed.Length, timeout, unconfirmed);
        }

        return AgentCommands.Empty;
    }

    // See AgentUriSet: value equality over the agents, independent of their order.
    public virtual bool Equals(AssignAgents? other)
        => other is not null && Destination == other.Destination
                             && AgentUriSet.AreEquivalent(AgentIds, other.AgentIds);

    public override int GetHashCode() => HashCode.Combine(Destination, AgentUriSet.HashOf(AgentIds));
}

internal record StopRemoteAgents(NodeDestination Destination, Uri[] AgentIds) : IAgentCommand
{
    public Guid? DestinationNodeId => Destination.NodeId;

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        var startAgents = new StopAgents(AgentIds);

        // GH-3748: this used to ride the flat default reply window no matter how many agents were in the
        // batch, and a stop can be as slow as a start (a projection shard mid-replay has to let go of its
        // work). Same scaled backstop as AssignAgents.
        var timeout = AgentBatchTimeouts.ReplyWindowFor(AgentIds.Length);
        await runtime.Agents.InvokeAsync<AgentsStopped>(Destination, startAgents, timeout);

        return AgentCommands.Empty;
    }

    // See AgentUriSet: value equality over the agents, independent of their order.
    public virtual bool Equals(StopRemoteAgents? other)
        => other is not null && Destination == other.Destination
                             && AgentUriSet.AreEquivalent(AgentIds, other.AgentIds);

    public override int GetHashCode() => HashCode.Combine(Destination, AgentUriSet.HashOf(AgentIds));
}

internal record StartAgents(Uri[] AgentUris) : IAgentCommand, ISerializable
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        // GH-3604 / D3: start the batch with bounded parallelism instead of serially. Daemon-agent starts are
        // I/O bound (database round-trips per shard), so a 50-agent chunk started one-at-a-time was seconds of
        // dead wall-clock that blew the reply window; a bounded fan-out lets the whole chunk converge quickly
        // without swamping the node.
        var successful = new System.Collections.Concurrent.ConcurrentBag<Uri>();

        var dop = Math.Max(1, runtime.Options.Durability.MaxAgentStartParallelism);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = dop,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(AgentUris, options, async (agentUri, token) =>
        {
            try
            {
                await runtime.Agents.StartLocallyAsync(agentUri);
                successful.Add(agentUri);
            }
            catch (Exception e)
            {
                runtime.Logger.LogError(e, "Failed to start requested agent {AgentUri}", agentUri);
            }
        });

        return [new AgentsStarted(successful.ToArray())];
    }

    public virtual bool Equals(StartAgents? other)
        => other is not null && AgentUriSet.AreEquivalent(AgentUris, other.AgentUris);

    public override int GetHashCode() => AgentUriSet.HashOf(AgentUris);

    public override string ToString()
    {
        return $"Start agents {AgentUris.Select(x => x.ToString()).Join(", ")}";
    }

    public byte[] Write()
    {
        return AgentUriList.Write(AgentUris);
    }

    public static object Read(byte[] bytes)
    {
        return new StartAgents(AgentUriList.Read(bytes));
    }
}

internal record AgentsStopped(Uri[] AgentUris) : IAgentCommand, ISerializable
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentCommands.Empty);
    }

    public virtual bool Equals(AgentsStopped? other)
        => other is not null && AgentUriSet.AreEquivalent(AgentUris, other.AgentUris);

    public override int GetHashCode() => AgentUriSet.HashOf(AgentUris);

    public byte[] Write()
    {
        return AgentUriList.Write(AgentUris);
    }

    public static object Read(byte[] bytes)
    {
        return new AgentsStopped(AgentUriList.Read(bytes));
    }
}

internal record StopAgents(Uri[] AgentUris) : IAgentCommand, ISerializable
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        var successful = new List<Uri>(AgentUris.Length);
        foreach (var agentUri in AgentUris)
        {
            try
            {
                await runtime.Agents.StopLocallyAsync(agentUri);
                successful.Add(agentUri);
            }
            catch (Exception e)
            {
                runtime.Logger.LogError(e, "Failed to start requested agent {AgentUri}", agentUri);
            }
        }

        return [new AgentsStopped(successful.ToArray())];
    }

    public virtual bool Equals(StopAgents? other)
        => other is not null && AgentUriSet.AreEquivalent(AgentUris, other.AgentUris);

    public override int GetHashCode() => AgentUriSet.HashOf(AgentUris);

    public override string ToString()
    {
        return $"Stop agents {AgentUris.Select(x => x.ToString()).Join(", ")}";
    }

    public byte[] Write()
    {
        return AgentUriList.Write(AgentUris);
    }

    public static object Read(byte[] bytes)
    {
        return new StopAgents(AgentUriList.Read(bytes));
    }
}