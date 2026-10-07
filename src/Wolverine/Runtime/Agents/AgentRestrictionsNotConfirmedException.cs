using JasperFx.Core;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     GH-4871. Thrown by <see cref="IAgentRuntime.ApplyRestrictionsAsync" /> when a restriction change that
///     should have put an agent to work -- a lifted pause, a removed pin, or a pin to a node -- was persisted and
///     evaluated, but the agent is assigned to no node once
///     <see cref="DurabilitySettings.AgentRestrictionConfirmationTimeout" /> has passed.
///
///     <para>
///     This is deliberately distinct from the <see cref="AggregateException" /> that method throws when a command
///     carrying the change <i>failed</i>. An unconfirmed start is not a failure to the leader -- <c>AssignAgents</c>
///     logs it and leaves the agent to the next evaluation (GH-3748 / GH-3750) -- but it is the whole answer to an
///     operator who just clicked Restart: nothing is running yet. Before this exception existed that outcome
///     returned exactly like a restart that worked, and monitoring tools acknowledged it as a success.
///     </para>
///
///     <para>
///     The restriction itself stands either way. The leader keeps re-evaluating assignments, so the state may
///     still converge; the destination node's log has the reason, under "Failed to start requested agent" or
///     "confirmed N of M requested agents".
///     </para>
/// </summary>
public class AgentRestrictionsNotConfirmedException : Exception
{
    public AgentRestrictionsNotConfirmedException(IReadOnlyList<Uri> agents) : base(buildMessage(agents))
    {
        Agents = agents;
    }

    /// <summary>
    ///     The agents the change released or pinned, and which are assigned to no node afterwards
    /// </summary>
    public IReadOnlyList<Uri> Agents { get; }

    private static string buildMessage(IReadOnlyList<Uri> agents)
    {
        return "The agent restriction change was persisted and evaluated, but the following agents are assigned to no node: "
               + agents.Select(x => x.ToString()).Join(", ")
               + ". The leader keeps re-evaluating assignments, so this may still converge. The destination node's "
               + "log has the reason, under 'Failed to start requested agent' or 'confirmed N of M requested agents'.";
    }
}
