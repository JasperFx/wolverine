using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4555, reported from a production blue/green rollout: the leader assigned Marten projection agents
/// to nodes whose own advertised capabilities did not contain them. The target node cannot build such an
/// agent -- <c>ArgumentOutOfRangeException: Unable to find a shard with path '...'</c> -- so no assignment
/// row is ever written, and the leader re-issues the identical placement on every evaluation. The reporter
/// measured ~504 failed starts an hour for over twelve hours.
///
/// <para>The fleet: three blue nodes and three green nodes sharing one control database, database per
/// tenant, the leader on blue. Green adds a projection blue has never heard of and bumps the version of
/// several others. The information needed to avoid all of this is already correct in
/// <c>wolverine_nodes.capabilities</c>.</para>
/// </summary>
public class mixed_version_fleet_capability_assignment_4555
{
    private static string DatabaseKey(Uri uri) => uri.Segments[2].Trim('/');

    private static Uri Projection(string database, string projection, string tenant) =>
        new($"event-subscriptions://marten/main/{database}/{projection}/All/{tenant}");

    private readonly string[] theTenants = Enumerable.Range(1, 7).Select(i => $"t{i}").ToArray();

    /// <summary>
    /// Database per tenant, so a tenant's database is its own group. Blue knows v22 and v23; green bumps
    /// those to v24 and v25 and adds "newprojection", which is the one in the report that never started.
    /// </summary>
    private Uri[] blueDeclares(string tenant) =>
    [
        Projection(tenant, "OrderProjection/v22", tenant),
        Projection(tenant, "InvoiceProjection/v23", tenant)
    ];

    private Uri[] greenOnlyDeclares(string tenant) =>
    [
        Projection(tenant, "OrderProjection/v24", tenant),
        Projection(tenant, "InvoiceProjection/v25", tenant),
        Projection(tenant, "newprojection", tenant)
    ];

    private Uri[] allAgents() =>
        theTenants.SelectMany(t => blueDeclares(t).Concat(greenOnlyDeclares(t))).ToArray();

    /// <summary>
    /// The grid as the blue leader sees it once green has joined: blue is running everything it declares,
    /// green has just come up and is running nothing yet.
    /// </summary>
    private AssignmentGrid theProductionGrid()
    {
        var grid = new AssignmentGrid();

        var blueCapabilities = theTenants.SelectMany(blueDeclares).ToArray();
        var greenCapabilities = theTenants
            .SelectMany(t => blueDeclares(t).Concat(greenOnlyDeclares(t))).ToArray();

        for (var i = 1; i <= 3; i++)
        {
            grid.WithNode(i, Guid.NewGuid()).HasCapabilities(blueCapabilities);
        }

        for (var i = 4; i <= 6; i++)
        {
            grid.WithNode(i, Guid.NewGuid()).HasCapabilities(greenCapabilities);
        }

        // blue has been serving traffic; spread its own agents over the three blue nodes the way a
        // settled cluster would have them
        var blueAgents = theTenants.SelectMany(blueDeclares).ToArray();
        for (var i = 0; i < blueAgents.Length; i++)
        {
            grid.Nodes.ElementAt(i % 3).Running(blueAgents[i]);
        }

        grid.WithAgents(allAgents());

        return grid;
    }

    /// <summary>
    /// The report's "Expected behaviour", first bullet, stated as an invariant over the whole grid: an
    /// agent whose URI is not in a node's advertised capabilities is not assigned to that node.
    /// </summary>
    private static void everyAgentRunsWhereItIsDeclared(AssignmentGrid grid)
    {
        foreach (var agent in grid.AllAgents.Where(x => x.AssignedNode != null))
        {
            agent.AssignedNode!.Capabilities.ShouldContain(agent.Uri,
                $"{agent.Uri} was assigned to node {agent.AssignedNode.AssignedId}, which does not declare it");
        }
    }

    [Fact]
    public void the_green_only_agents_are_never_assigned_to_a_blue_node()
    {
        var grid = theProductionGrid();

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        everyAgentRunsWhereItIsDeclared(grid);

        foreach (var tenant in theTenants)
        foreach (var uri in greenOnlyDeclares(tenant))
        {
            var node = grid.AgentFor(uri).AssignedNode;
            node.ShouldNotBeNull($"{uri} was left unassigned, so the projection never runs");
            node!.AssignedId.ShouldBeGreaterThan(3, $"{uri} was assigned to a blue node");
        }
    }

    [Fact]
    public void a_tenant_provisioned_after_blue_started_does_not_drag_the_green_agents_onto_blue()
    {
        // The trigger in the report, and the one GH-4562 named: a tenant provisioned after the blue nodes
        // captured their capability snapshot. Blue RUNS that tenant's agents without declaring them, so
        // before GH-4563 the grandfathering made blue a candidate for the tenant's whole capability
        // partition -- green's new projection included.
        var grid = new AssignmentGrid();

        const string latecomer = "t-late";
        var blueCapabilities = theTenants.SelectMany(blueDeclares).ToArray();
        var greenCapabilities = theTenants.Concat([latecomer])
            .SelectMany(t => blueDeclares(t).Concat(greenOnlyDeclares(t))).ToArray();

        for (var i = 1; i <= 3; i++)
        {
            grid.WithNode(i, Guid.NewGuid()).HasCapabilities(blueCapabilities);
        }

        for (var i = 4; i <= 6; i++)
        {
            grid.WithNode(i, Guid.NewGuid()).HasCapabilities(greenCapabilities);
        }

        // blue is running the latecomer's old-version agents although it never declared them
        grid.Nodes.First().Running(blueDeclares(latecomer));

        var agents = allAgents().Concat(blueDeclares(latecomer)).Concat(greenOnlyDeclares(latecomer)).ToArray();
        grid.WithAgents(agents);

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        foreach (var uri in greenOnlyDeclares(latecomer))
        {
            var node = grid.AgentFor(uri).AssignedNode;
            node.ShouldNotBeNull($"{uri} was left unassigned");
            node!.Capabilities.ShouldContain(uri,
                $"{uri} was assigned to node {node.AssignedId}, which cannot build it");
        }
    }

    [Fact]
    public void nothing_is_stranded_while_the_two_fleets_overlap()
    {
        var grid = theProductionGrid();

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        grid.AllAgents.Where(x => x.AssignedNode == null)
            .Select(x => x.Uri.ToString())
            .ShouldBeEmpty();
    }

    [Fact]
    public void the_leader_says_so_when_it_issues_an_assignment_the_node_cannot_honor()
    {
        // The tripwire. The placement bug is fixed, so this can only fire on a future regression -- but the
        // reporter had to reconstruct twelve hours of this from application logs because the leader, which
        // made the decision, said nothing at all.
        var grid = theProductionGrid();
        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        var green = Projection("t1", "newprojection", "t1");
        grid.Nodes.First().Assign(green);
        var issued = new[] { grid.AgentFor(green) };

        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        var reported = 0;
        NodeAgentController.WarnAboutUndeclaredAssignments(grid, issued, logger, ref reported);
        reported.ShouldBe(1);

        // latched on the count, so a steady state is not re-logged on every evaluation
        NodeAgentController.WarnAboutUndeclaredAssignments(grid, issued, logger, ref reported);

        logger.ReceivedWithAnyArgs(1).Log(default, default, default!, default, default!);
    }

    [Fact]
    public void the_tripwire_stays_quiet_on_a_correct_grid()
    {
        var grid = theProductionGrid();
        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        var reported = 0;
        NodeAgentController.WarnAboutUndeclaredAssignments(grid, grid.AllAgents, logger, ref reported);

        reported.ShouldBe(0);
        logger.DidNotReceiveWithAnyArgs().Log(default, default, default!, default, default!);
    }

    [Fact]
    public void the_tripwire_ignores_an_agent_no_node_declares()
    {
        // Durability agents are advertised at the family level rather than per URI, and the GH-3341 rescue
        // deliberately places an agent no surviving node declares. Neither is a defect to shout about.
        var grid = new AssignmentGrid();
        var node = grid.WithNode(1, Guid.NewGuid());
        var undeclared = Projection("t1", "OrderProjection/v22", "t1");
        grid.WithAgents(undeclared);
        node.Assign(undeclared);

        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        var reported = 0;
        NodeAgentController.WarnAboutUndeclaredAssignments(grid, grid.AllAgents, logger, ref reported);

        reported.ShouldBe(0);
        logger.DidNotReceiveWithAnyArgs().Log(default, default, default!, default, default!);
    }

    [Fact]
    public void the_placement_settles_instead_of_being_re_sent_every_evaluation()
    {
        // The defect was not one bad placement but an unbounded loop: the blue node could never build what
        // it was handed, so nothing was ever recorded as assigned and the next evaluation made the same
        // decision. Replaying the result as the next evaluation's starting grid is how that shows up here.
        var first = theProductionGrid();
        first.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        var second = new AssignmentGrid();
        var blueCapabilities = theTenants.SelectMany(blueDeclares).ToArray();
        var greenCapabilities = theTenants
            .SelectMany(t => blueDeclares(t).Concat(greenOnlyDeclares(t))).ToArray();

        for (var i = 1; i <= 6; i++)
        {
            var node = second.WithNode(i, Guid.NewGuid())
                .HasCapabilities(i <= 3 ? blueCapabilities : greenCapabilities);

            // only what the node could actually start is running on the second pass
            node.Running(first.AllAgents
                .Where(x => x.AssignedNode?.AssignedId == i && x.AssignedNode.Capabilities.Contains(x.Uri))
                .Select(x => x.Uri).ToArray());
        }

        second.WithAgents(allAgents());
        second.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        everyAgentRunsWhereItIsDeclared(second);

        foreach (var agent in second.AllAgents)
        {
            agent.AssignedNode!.AssignedId.ShouldBe(first.AgentFor(agent.Uri).AssignedNode!.AssignedId,
                $"{agent.Uri} moved again on the second evaluation, so the grid never settles");
        }
    }
}
