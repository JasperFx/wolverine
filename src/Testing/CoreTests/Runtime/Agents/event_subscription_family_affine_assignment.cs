using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// JasperFx/marten#4806: when a sharded store opts into <see cref="IEventStore.GroupAgentAssignmentsByDatabase"/>,
/// <see cref="EventSubscriptionAgentFamily.EvaluateAssignmentsAsync"/> must assign that store's per-(shard, tenant)
/// agents with database affinity (a database's agents kept on one node, bounded by
/// <see cref="IEventStore.MaxNodesPerDatabaseForAgents"/>) instead of the default even blue/green spread. These
/// are fast unit tests: agents are registered directly on an <see cref="AssignmentGrid"/> and the store flags are
/// stubbed, so no host or database is required.
/// </summary>
public class event_subscription_family_affine_assignment
{
    // Agent URI grammar is event-subscriptions://{type}/{name}/{databaseId}/{shard...} (see EventStoreAgents.UriFor).
    private static Uri Agent(string db, string tenant) =>
        new($"event-subscriptions://marten/main/{db}/Proj:All:{tenant}");

    private static EventSubscriptionAgentFamily FamilyFor(bool affine, int maxNodesPerDatabase = 1)
    {
        var store = Substitute.For<IEventStore>();
        store.Identity.Returns(new EventStoreIdentity("main", "marten"));
        store.GroupAgentAssignmentsByDatabase.Returns(affine);
        store.MaxNodesPerDatabaseForAgents.Returns(maxNodesPerDatabase);
        return new EventSubscriptionAgentFamily(new[] { store }, Array.Empty<IObserver<JasperFx.Events.Projections.ShardState>>());
    }

    [Fact]
    public async Task keeps_a_databases_agents_together_when_the_store_opts_into_affinity()
    {
        var family = FamilyFor(affine: true);

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        // One shard database with two tenants: even distribution would split it across the two nodes.
        grid.WithAgents(Agent("dbShared", "t1"), Agent("dbShared", "t2"));

        await family.EvaluateAssignmentsAsync(grid);

        var nodes = new[] { "t1", "t2" }
            .Select(t => grid.AgentFor(Agent("dbShared", t)).AssignedNode)
            .Distinct()
            .ToList();

        nodes.Count.ShouldBe(1, "an affine store must keep a shard database's agents on a single node");
        nodes[0].ShouldNotBeNull();
    }

    [Fact]
    public async Task spreads_a_databases_agents_evenly_when_the_store_does_not_opt_in()
    {
        var family = FamilyFor(affine: false);

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(Agent("dbShared", "t1"), Agent("dbShared", "t2"));

        await family.EvaluateAssignmentsAsync(grid);

        var nodes = new[] { "t1", "t2" }
            .Select(t => grid.AgentFor(Agent("dbShared", t)).AssignedNode)
            .Distinct()
            .ToList();

        nodes.Count.ShouldBe(2, "without affinity even distribution splits a two-agent database across both nodes");
    }

    [Fact]
    public async Task fans_a_heavy_database_out_across_the_stores_max_nodes_bound()
    {
        // The family must pass the store's MaxNodesPerDatabaseForAgents through to the grid so a heavy database
        // parallelizes across up to that many nodes — not one (strict affinity) and not all three (even spread).
        var family = FamilyFor(affine: true, maxNodesPerDatabase: 2);

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithNode(3, Guid.NewGuid());
        var agents = Enumerable.Range(1, 6).Select(t => Agent("dbHeavy", "t" + t)).ToArray();
        grid.WithAgents(agents);

        await family.EvaluateAssignmentsAsync(grid);

        var nodesUsed = agents.Select(a => grid.AgentFor(a).AssignedNode).Distinct().ToList();
        nodesUsed.Count.ShouldBe(2, "the family must honor the store's fan-out bound of 2");
        nodesUsed.ShouldAllBe(n => n != null);
    }

    [Fact]
    public void database_key_groups_a_databases_agents_and_separates_databases()
    {
        var t1 = EventSubscriptionAgentFamily.DatabaseKeyOf(Agent("claims1", "t1"));
        var t2 = EventSubscriptionAgentFamily.DatabaseKeyOf(Agent("claims1", "t2"));
        var other = EventSubscriptionAgentFamily.DatabaseKeyOf(Agent("claims2", "t1"));

        t1.ShouldBe("marten/main/claims1");
        t2.ShouldBe(t1, "two tenants on the same shard database must share a group key");
        other.ShouldNotBe(t1, "a different shard database must be a different group");
    }

    [Fact]
    public void database_key_falls_back_to_the_whole_uri_when_there_is_no_database_segment()
    {
        // Defensive: a URI without the {databaseId} segment (fewer than 3 path segments) can't be grouped by
        // database, so each such agent is its own group rather than silently collapsing together.
        var uri = new Uri("event-subscriptions://marten/main");
        EventSubscriptionAgentFamily.DatabaseKeyOf(uri).ShouldBe(uri.AbsoluteUri);
    }
}

/// <summary>
/// <see cref="EventStoreAgents"/> surfaces the affine-assignment flags from its underlying
/// <see cref="IEventStore"/> unchanged; the agent family reads them off the wrapper. See JasperFx/marten#4806.
/// </summary>
public class event_store_agents_affinity_passthrough
{
    private static EventStoreAgents AgentsFor(bool affine, int maxNodes)
    {
        var store = Substitute.For<IEventStore>();
        store.Identity.Returns(new EventStoreIdentity("main", "marten"));
        store.GroupAgentAssignmentsByDatabase.Returns(affine);
        store.MaxNodesPerDatabaseForAgents.Returns(maxNodes);
        return new EventStoreAgents(store, Array.Empty<IObserver<JasperFx.Events.Projections.ShardState>>());
    }

    [Fact]
    public void surfaces_group_by_database_and_max_nodes_from_the_store()
    {
        var agents = AgentsFor(affine: true, maxNodes: 3);

        agents.GroupAgentAssignmentsByDatabase.ShouldBeTrue();
        agents.MaxNodesPerDatabaseForAgents.ShouldBe(3);
    }

    [Fact]
    public void defaults_flow_through_for_a_non_affine_store()
    {
        var agents = AgentsFor(affine: false, maxNodes: 1);

        agents.GroupAgentAssignmentsByDatabase.ShouldBeFalse();
        agents.MaxNodesPerDatabaseForAgents.ShouldBe(1);
    }
}
