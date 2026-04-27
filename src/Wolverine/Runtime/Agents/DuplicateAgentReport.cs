namespace Wolverine.Runtime.Agents;

/// <summary>
/// Recorded by <see cref="AssignmentGrid"/> when a single agent URI shows up
/// in more than one <see cref="WolverineNode.ActiveAgents"/> list during a
/// heartbeat tick. Symptom of a leader split-brain (GH-2602): two leaders
/// dispatched <c>AssignAgent</c> for the same agent in close succession.
/// The (now-correct) leader uses these reports to emit a
/// <c>StopRemoteAgent</c> targeting <see cref="ExistingNode"/>, leaving the
/// freshest copy on <see cref="NewNode"/> running.
/// </summary>
internal sealed record DuplicateAgentReport(
    Uri AgentUri,
    AssignmentGrid.Node ExistingNode,
    AssignmentGrid.Node NewNode);
