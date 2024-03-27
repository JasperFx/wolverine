namespace Wolverine.Runtime.Agents;

public record TryAssumeLeadership : IAgentCommand
{
    public Guid? CurrentLeaderId { get; set; }
    public Guid? CandidateId { get; set; }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        if (CandidateId == null)
        {
            return await runtime.Agents.AssumeLeadershipAsync(CurrentLeaderId);
        }

        await runtime.Agents.InvokeAsync(CandidateId.Value, new TryAssumeLeadership());
        return AgentCommands.Empty;

    }
}