namespace Wolverine.Runtime.Agents;

public interface IAgentController
{
    string Scheme { get; }
    ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync();
    ValueTask<IAgent> BuildAgentAsync(Uri uri);
    ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync();
    ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments);
}