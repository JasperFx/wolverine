using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T> : IAgentFamily
{
    private readonly Uri _defaultAgent = new($"{DurabilityAgent.AgentScheme}://{TransportConstants.Default}");

    public string Scheme => DurabilityAgent.AgentScheme;

    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        var list = new List<Uri> { _defaultAgent };
        return new ValueTask<IReadOnlyList<Uri>>(list);
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime runtime)
    {
        if (uri != _defaultAgent)
        {
            throw new ArgumentOutOfRangeException(nameof(uri));
        }

        var agent = new DurabilityAgent(TransportConstants.Default, runtime, this);

        return new ValueTask<IAgent>(agent);
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        var list = new List<Uri> { _defaultAgent };
        return new ValueTask<IReadOnlyList<Uri>>(list);
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        // run on leader
        assignments.RunOnLeader(_defaultAgent);

        return ValueTask.CompletedTask;
    }
}