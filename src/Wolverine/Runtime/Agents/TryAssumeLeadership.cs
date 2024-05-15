namespace Wolverine.Runtime.Agents;

public record TryAssumeLeadership : IAgentCommand, ISerializable
{
    public Guid? CurrentLeaderId { get; set; }
    public Guid? CandidateId { get; set; }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        if (CandidateId == null)
        {
            try
            {
                return await runtime.Agents.AssumeLeadershipAsync(CurrentLeaderId);
            }
            catch (OperationCanceledException)
            {
                // Just shutting down, don't log here
            }
        }

        try
        {
            await runtime.Agents.InvokeAsync(CandidateId.Value, new TryAssumeLeadership());
        }
        catch (UnknownWolverineNodeException)
        {
            return [new TryAssumeLeadership()];
        }

        return AgentCommands.Empty;
    }

    public byte[] Write()
    {
        return (CurrentLeaderId ?? Guid.Empty).ToByteArray().Concat((CandidateId ?? Guid.Empty).ToByteArray())
            .ToArray();
    }

    public static object Read(byte[] bytes)
    {
        var leaderId = new Guid(bytes[..16]);
        var candidateId = new Guid(bytes[16..]);
        return new TryAssumeLeadership
        {
            CurrentLeaderId = leaderId == Guid.Empty ? null : leaderId,
            CandidateId = candidateId == Guid.Empty ? null : candidateId
        };
    }
}