using Wolverine.Logging;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Event once used for test automation to "wait" for all assignments to be made
/// </summary>
/// <remarks>
///     GH-5078: nothing in Wolverine constructs or publishes this any more, so a subscriber to
///     <c>runtime.Tracker</c> never sees it. The leader reports every evaluation through
///     <see cref="IWolverineObserver.AssignmentsChanged" />.
/// </remarks>
/// <param name="Commands"></param>
[Obsolete("Nothing publishes AgentAssignmentsChanged any more. Agent assignment changes are reported through IWolverineObserver.AssignmentsChanged(AssignmentGrid, AgentCommands); wrap the runtime's Observer before the host starts. See GH-5078.")]
public record AgentAssignmentsChanged
    (IReadOnlyList<IAgentCommand> Commands, AssignmentGrid Assignments) : IWolverineEvent
{
    public void ModifyState(WolverineTracker tracker)
    {
        // nothing
    }
}