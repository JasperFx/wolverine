using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.ComplianceTests;

public class FakeAgentFamily : IStaticAgentFamily
{
    public FakeAgentFamily()
    {
    }

    public FakeAgentFamily(string scheme)
    {
        Scheme = scheme;
    }

    /// <summary>
    ///     GH-3779: a family of an arbitrary size. The twelve hard-coded <see cref="Names" /> are fine for
    ///     asserting the shape of a distribution, but GH-3753 is about ~6,500 agents across five nodes, and
    ///     the pathologies there — a node left holding everything while its peers sit at zero, an assigned
    ///     count that goes backwards — only emerge at a scale where one assignment wave cannot complete
    ///     inside one evaluation cycle.
    /// </summary>
    public FakeAgentFamily(string scheme, int agentCount)
    {
        if (agentCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(agentCount), agentCount,
                "An agent family needs at least one agent");
        }

        Scheme = scheme;

        // Zero-padded so the lexical order matches the numeric one; several assertions read a lot better
        // against a sorted agent list.
        AgentNames = Enumerable.Range(0, agentCount).Select(i => $"agent-{i:D5}").ToArray();
    }

    /// <summary>
    ///     A family over explicit agent names — names may carry path segments (e.g. <c>db01/v22/t1</c>) so a
    ///     test can model grouped, versioned agents the way <c>EventSubscriptionAgentFamily</c> URIs do.
    /// </summary>
    public FakeAgentFamily(string scheme, IReadOnlyList<string> agentNames)
    {
        Scheme = scheme;
        AgentNames = agentNames;
    }

    public string Scheme { get; } = "fake";

    public static string[] Names =
    [
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
    ];

    /// <summary>
    ///     The agent names this particular family exposes. Defaults to the shared <see cref="Names" /> so that
    ///     every test predating the count-taking constructor is unaffected.
    /// </summary>
    public IReadOnlyList<string> AgentNames { get; } = Names;

    /// <summary>
    ///     GH-3779: per-agent start cost, so a long TAIL can be simulated rather than a uniform delay. The
    ///     field distribution that broke GH-3753 was p50 27s / p95 82s / tail 215s, and it is the tail that
    ///     does the damage — a uniformly slow family converges late but converges, while a handful of very
    ///     slow starts is what outlives a reply window and gets re-decided.
    /// </summary>
    public Func<Uri, TimeSpan>? StartDelayPolicy { get; set; }

    public LightweightCache<Uri, FakeAgent> Agents { get; } = new(x => new FakeAgent(x));

    /// <summary>
    ///     Override how this family distributes its agents over the grid. Defaults to the capability-blind
    ///     <see cref="AssignmentGrid.DistributeEvenly(string)" />; a test simulating a sharded store sets this to
    ///     <see cref="AssignmentGrid.DistributeByGroupAffinity(string, Func{Uri, string})" /> the way
    ///     <c>EventSubscriptionAgentFamily</c> does for a multi-database store.
    /// </summary>
    public Action<AssignmentGrid>? Distribution { get; set; }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        if (Distribution != null)
        {
            Distribution(assignments);
        }
        else
        {
            assignments.DistributeEvenly(Scheme);
        }

        return new ValueTask();
    }

    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        var agents = AllAgentUris();
        return ValueTask.FromResult((IReadOnlyList<Uri>)agents);
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime runtime)
    {
        var agent = Agents[uri];

        if (StartDelayPolicy != null)
        {
            agent.StartDelay = StartDelayPolicy(uri);
        }

        return new ValueTask<IAgent>(agent);
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        var agents = AllAgentUris();
        return ValueTask.FromResult((IReadOnlyList<Uri>)agents);
    }

    public Uri[] AllAgentUris()
    {
        return AgentNames.Select(x => new Uri($"{Scheme}://{x}")).ToArray();
    }
}
