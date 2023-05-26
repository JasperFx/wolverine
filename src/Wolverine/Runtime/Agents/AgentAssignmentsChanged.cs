using JasperFx.Core;
using Wolverine.Logging;

namespace Wolverine.Runtime.Agents;

/// <summary>
/// Event used for test automation to "wait" for all assignments to be made
/// </summary>
/// <param name="Commands"></param>
public record AgentAssignmentsChanged(IReadOnlyList<IAgentCommand> Commands) : IWolverineEvent
{
    public void ModifyState(WolverineTracker tracker)
    {
        // nothing
    }
}
