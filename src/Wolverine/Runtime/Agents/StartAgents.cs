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
        return Encoding.UTF8.GetBytes(AgentUris.Select(x => x.ToString()).Join(","));
    }

    public static object Read(byte[] bytes)
    {
        var uris = Encoding.UTF8.GetString(bytes).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new Uri(x)).ToArray();
        return new AgentsStarted(uris);
    }
}

internal record AssignAgents(NodeDestination Destination, Uri[] AgentIds) : IAgentCommand
{
    public Guid? DestinationNodeId => Destination.NodeId;

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        var startAgents = new StartAgents(AgentIds);

        // GH-3604 / D3: scale the reply timeout with the chunk size as a backstop. The receiving node starts
        // the batch with bounded parallelism, so the reply normally arrives quickly, but a large chunk of
        // slow daemon-agent starts must not be cut off by the default fixed request/reply window.
        var timeout = 30.Seconds() + AgentIds.Length.Seconds();
        var response = await runtime.Agents.InvokeAsync<AgentsStarted>(Destination, startAgents, timeout);

        if (response == null)
        {
            return AgentCommands.Empty;
        }

        runtime.Logger.LogInformation("Successfully started agents {Agents} on node {NodeNumber}", AgentIds, runtime.Options.Durability.AssignedNodeNumber);

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
        await runtime.Agents.InvokeAsync<AgentsStopped>(Destination, startAgents);

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
        return Encoding.UTF8.GetBytes(AgentUris.Select(x => x.ToString()).Join(","));
    }

    public static object Read(byte[] bytes)
    {
        var agents = Encoding.UTF8.GetString(bytes).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new Uri(x)).ToArray();
        return new StartAgents(agents);
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
        return Encoding.UTF8.GetBytes(AgentUris.Select(x => x.ToString()).Join(","));
    }

    public static object Read(byte[] bytes)
    {
        var uris = Encoding.UTF8.GetString(bytes).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new Uri(x)).ToArray();
        return new AgentsStopped(uris);
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
        return Encoding.UTF8.GetBytes(AgentUris.Select(x => x.ToString()).Join(","));
    }

    public static object Read(byte[] bytes)
    {
        var agents = Encoding.UTF8.GetString(bytes).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new Uri(x)).ToArray();
        return new StopAgents(agents);
    }
}