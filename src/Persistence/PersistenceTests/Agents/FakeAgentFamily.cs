using JasperFx.Core;
using Wolverine.Runtime.Agents;

namespace PersistenceTests.Agents;

public class FakeAgentFamily : IAgentFamily
{
    public string Scheme { get; } = "fake";

    public static string[] Names = new string[]
    {
        "one",
        "two",
        "three",
        "four",
        "five",
        "six",
        "seven",
        "eight",
        "nine",
        "ten",
        "eleven",
        "twelve"
    };

    public LightweightCache<Uri, FakeAgent> Agents { get; } = new(x => new FakeAgent(x));
    
    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(Scheme);
        return new ValueTask();
    }

    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        var agents = Names.Select(x => new Uri($"fake://{x}")).ToArray();
        return ValueTask.FromResult((IReadOnlyList<Uri>)agents);
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri)
    {
        return new ValueTask<IAgent>(Agents[uri]);
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        var agents = AllAgentUris();
        return ValueTask.FromResult((IReadOnlyList<Uri>)agents);
    }

    public static Uri[] AllAgentUris()
    {
        return Names.Select(x => new Uri($"fake://{x}")).ToArray();
    }
}