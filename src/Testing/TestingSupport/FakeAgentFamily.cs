using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace TestingSupport;

public class FakeAgentFamily : IStaticAgentFamily
{
    public FakeAgentFamily()
    {
    }

    public FakeAgentFamily(string scheme)
    {
        Scheme = scheme;
    }

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
        var agents = Names.Select(x => new Uri($"{Scheme}://{x}")).ToArray();
        return ValueTask.FromResult((IReadOnlyList<Uri>)agents);
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime runtime)
    {
        return new ValueTask<IAgent>(Agents[uri]);
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        var agents = AllAgentUris();
        return ValueTask.FromResult((IReadOnlyList<Uri>)agents);
    }

    public Uri[] AllAgentUris()
    {
        return Names.Select(x => new Uri($"{Scheme}://{x}")).ToArray();
    }
}