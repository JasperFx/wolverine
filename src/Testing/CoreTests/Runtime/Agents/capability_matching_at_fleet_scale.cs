using JasperFx.Core;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
///     Matching agents to capable nodes used to scan every node's capability list once per agent. With
///     per-tenant projection agents that list holds tens of thousands of entries, so a blue/green
///     evaluation of ~62,000 agents over a handful of nodes kept a new leader busy for over an hour.
/// </summary>
public class capability_matching_at_fleet_scale
{
    private const string Scheme = "event-subscriptions";

    private static Uri agent(int shard, string projection, int tenant) =>
        new($"event-subscriptions://marten/main/database-productie-{shard}/{projection}/all/{tenant:D8}");

    private static string databaseKey(Uri uri) => uri.Segments[2].Trim('/');

    private static WolverineNode nodeDeclaring(int number, IEnumerable<Uri> capabilities) => new()
    {
        NodeId = Guid.NewGuid(),
        AssignedNodeNumber = number,
        Capabilities = capabilities.ToList()
    };

    [Fact]
    public void candidate_nodes_are_exactly_the_nodes_declaring_the_agent()
    {
        var shared = agent(1, "orders/v1", 1);
        var blueOnly = agent(1, "invoices/v1", 1);
        var greenOnly = agent(1, "invoices/v2", 1);
        var nobody = agent(2, "orders/v1", 2);

        var grid = new AssignmentGrid();
        var blue = grid.WithNode(nodeDeclaring(1, [shared, blueOnly]));
        var green1 = grid.WithNode(nodeDeclaring(2, [shared, greenOnly]));
        var green2 = grid.WithNode(nodeDeclaring(3, [greenOnly, shared]));
        grid.WithAgents(shared, blueOnly, greenOnly, nobody);

        grid.MatchAgentsToCapableNodesFor(Scheme);

        grid.AgentFor(shared).CandidateNodes.ShouldBe([blue, green1, green2]);
        grid.AgentFor(blueOnly).CandidateNodes.ShouldBe([blue]);
        grid.AgentFor(greenOnly).CandidateNodes.ShouldBe([green1, green2]);
        grid.AgentFor(nobody).CandidateNodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_blue_green_distribution_of_sixty_thousand_agents_takes_seconds()
    {
        // 64 shard databases, 2,600 tenants, 23 projections: 59,800 agents per version, of which only one
        // projection differs between the versions -- 62,400 distinct agents in the grid
        var projections = Enumerable.Range(1, 23).Select(i => $"projection-{i}").ToArray();
        var tenants = Enumerable.Range(1, 2600).Select(i => (Shard: i % 64 + 1, Tenant: 1_050_000 + i)).ToArray();

        IEnumerable<Uri> declared(string version) => tenants.SelectMany(t =>
            projections.Select(p => agent(t.Shard, $"{p}/{(p == "projection-1" ? version : "v1")}", t.Tenant)));

        var blue = declared("v30").ToList();
        var green = declared("v31").ToList();

        var grid = new AssignmentGrid();
        for (var i = 1; i <= 3; i++) grid.WithNode(nodeDeclaring(i, blue));
        for (var i = 4; i <= 5; i++) grid.WithNode(nodeDeclaring(i, green));
        grid.WithAgents(blue.Union(green).ToArray());

        // The old list scan needs tens of minutes here; WaitAsync turns that into a failure, not a hung run
        await Task.Run(() => grid.DistributeByGroupAffinity(Scheme, databaseKey, _ => true), TestContext.Current.CancellationToken)
            .WaitAsync(15.Seconds(), TestContext.Current.CancellationToken);

        grid.AllAgents.ShouldAllBe(a => a.AssignedNode != null && a.AssignedNode.Declares(a.Uri));
    }
}
