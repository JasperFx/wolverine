namespace Wolverine.Runtime.Agents;

/// <summary>
///     GH-3748 / GH-3750: asks a node which of the named agents are actually running there right now.
///
///     <para>This is the leader's progress probe for an in-flight batched agent command. The old protocol
///     gave the leader exactly one bit of information — the final <see cref="AgentsStarted" /> /
///     <see cref="AgentsStopped" /> reply — and when slow agent work outran the reply window, the leader
///     had to treat the whole chunk as failed and re-place agents that were mid-start on the very node it
///     gave up on. Answering this query costs the destination a read of its in-memory agent registry, so
///     the leader can wait on <i>observed progress</i> instead of a clock.</para>
///
///     <para>The report names the requested agents that are currently running. The complement is
///     deliberately direction-agnostic: a leader waiting on starts reads the running set as confirmation,
///     and a leader waiting on stops reads the missing set as confirmation — an agent this node has never
///     seen is exactly as stopped as one it just stopped.</para>
/// </summary>
internal record QueryAgentPresence(Uri[] AgentUris) : IAgentCommand, ISerializable
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        var running = new HashSet<Uri>(runtime.Agents.AllRunningAgentUris());
        var present = AgentUris.Where(running.Contains).ToArray();

        return Task.FromResult<AgentCommands>([new AgentPresenceReport(present)]);
    }

    // See AgentUriSet: value equality over the agents, independent of their order.
    public virtual bool Equals(QueryAgentPresence? other)
        => other is not null && AgentUriSet.AreEquivalent(AgentUris, other.AgentUris);

    public override int GetHashCode() => AgentUriSet.HashOf(AgentUris);

    public byte[] Write()
    {
        return AgentUriList.Write(AgentUris);
    }

    public static object Read(byte[] bytes)
    {
        return new QueryAgentPresence(AgentUriList.Read(bytes));
    }
}

/// <summary>
///     The answer to <see cref="QueryAgentPresence" />: which of the queried agents are running on the
///     answering node. See that type for the protocol.
/// </summary>
internal record AgentPresenceReport(Uri[] Running) : IAgentCommand, ISerializable
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentCommands.Empty);
    }

    public virtual bool Equals(AgentPresenceReport? other)
        => other is not null && AgentUriSet.AreEquivalent(Running, other.Running);

    public override int GetHashCode() => AgentUriSet.HashOf(Running);

    public byte[] Write()
    {
        return AgentUriList.Write(Running);
    }

    public static object Read(byte[] bytes)
    {
        return new AgentPresenceReport(AgentUriList.Read(bytes));
    }
}
