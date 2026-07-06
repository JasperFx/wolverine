using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// JasperFx/jasperfx#486: <see cref="EventSubscriptionAgentFamily.EvaluateAssignmentsAsync"/> derives the
/// distribution style from each store's <see cref="IEventStore.DatabaseCardinality"/> — no opt-in flag. A
/// store backed by multiple databases (Static/DynamicMultiple) gets database-affine assignment (a
/// database's agents kept together on one node); a single-database store keeps the even blue/green spread;
/// both styles coexist store-by-store within the one scheme. These are fast unit tests: agents are
/// registered directly on an <see cref="AssignmentGrid"/> and the store is stubbed, so no host or database
/// is required.
/// </summary>
public class event_subscription_family_cardinality_assignment
{
    // Agent URI grammar is event-subscriptions://{type}/{name}/{databaseId}/{shard...} (see EventStoreAgents.UriFor).
    private static Uri Agent(string db, string tenant) =>
        new($"event-subscriptions://marten/main/{db}/proj/all/{tenant}");

    private static Uri SingleStoreAgent(string projection) =>
        new($"event-subscriptions://other/single/localhost.app/{projection}/all");

    private static IEventStore StoreWith(string name, string type, DatabaseCardinality cardinality)
    {
        var store = Substitute.For<IEventStore>();
        store.Identity.Returns(new EventStoreIdentity(name, type));
        store.DatabaseCardinality.Returns(cardinality);
        store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(Task.FromResult<EventStoreUsage?>(null));
        return store;
    }

    private static EventSubscriptionAgentFamily FamilyFor(params IEventStore[] stores)
        => new(stores, Array.Empty<IObserver<ShardState>>());

    [Fact]
    public async Task keeps_a_databases_agents_together_when_the_store_is_multi_database()
    {
        var family = FamilyFor(StoreWith("main", "marten", DatabaseCardinality.StaticMultiple));

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

        nodes.Count.ShouldBe(1, "a multi-database store must keep a shard database's agents on a single node");
        nodes[0].ShouldNotBeNull();
    }

    [Theory]
    [InlineData(DatabaseCardinality.Single)]
    [InlineData(DatabaseCardinality.None)]
    public async Task spreads_agents_evenly_when_the_store_is_not_multi_database(DatabaseCardinality cardinality)
    {
        var family = FamilyFor(StoreWith("main", "marten", cardinality));

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(Agent("dbShared", "t1"), Agent("dbShared", "t2"));

        await family.EvaluateAssignmentsAsync(grid);

        var nodes = new[] { "t1", "t2" }
            .Select(t => grid.AgentFor(Agent("dbShared", t)).AssignedNode)
            .Distinct()
            .ToList();

        nodes.Count.ShouldBe(2, "without a multi-database cardinality even distribution splits a two-agent database across both nodes");
    }

    [Fact]
    public async Task dynamic_multiple_is_also_distributed_affine()
    {
        var family = FamilyFor(StoreWith("main", "marten", DatabaseCardinality.DynamicMultiple));

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(Agent("dbShared", "t1"), Agent("dbShared", "t2"));

        await family.EvaluateAssignmentsAsync(grid);

        new[] { "t1", "t2" }
            .Select(t => grid.AgentFor(Agent("dbShared", t)).AssignedNode)
            .Distinct()
            .Count()
            .ShouldBe(1);
    }

    [Fact]
    public async Task mixed_stores_are_distributed_independently_affine_and_even()
    {
        // One multi-database store (affine) and one single-database store (even) in the same application.
        var family = FamilyFor(
            StoreWith("main", "marten", DatabaseCardinality.StaticMultiple),
            StoreWith("single", "other", DatabaseCardinality.Single));

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());

        // Multi-database store: two shard databases, two tenants each.
        var affineAgents = new List<Uri>();
        foreach (var db in new[] { "db1", "db2" })
        foreach (var tenant in new[] { "t1", "t2" })
            affineAgents.Add(Agent(db, tenant));

        // Single-database store: two projection agents.
        var evenAgents = new[] { SingleStoreAgent("proj1"), SingleStoreAgent("proj2") };

        grid.WithAgents(affineAgents.Concat(evenAgents).ToArray());

        await family.EvaluateAssignmentsAsync(grid);

        // Affine store: each database whole on one node.
        foreach (var db in new[] { "db1", "db2" })
        {
            new[] { "t1", "t2" }
                .Select(t => grid.AgentFor(Agent(db, t)).AssignedNode)
                .Distinct()
                .Count()
                .ShouldBe(1, $"all agents of {db} must be on a single node");
        }

        // Even store: its two agents split 1/1 across the nodes, unaffected by the affine pass.
        var evenNodes = evenAgents.Select(u => grid.AgentFor(u).AssignedNode).ToList();
        evenNodes.ShouldAllBe(n => n != null);
        evenNodes.Distinct().Count().ShouldBe(2, "the single-database store keeps the even spread");
    }

    [Fact]
    public async Task mixed_stores_honor_blue_green_capabilities_on_the_even_store()
    {
        var family = FamilyFor(
            StoreWith("main", "marten", DatabaseCardinality.StaticMultiple),
            StoreWith("single", "other", DatabaseCardinality.Single));

        var evenAgents = new[] { SingleStoreAgent("proj1"), SingleStoreAgent("proj2") };

        var grid = new AssignmentGrid();
        // Only node 1 declares the single-database store's agents as capabilities (blue/green rollout);
        // neither node declares the multi-database store's agents, so THAT pass sees homogeneous
        // (empty) capabilities and stays capability-blind.
        grid.WithNode(1, Guid.NewGuid()).HasCapabilities(evenAgents);
        grid.WithNode(2, Guid.NewGuid());

        var affineAgents = new[] { Agent("db1", "t1"), Agent("db1", "t2") };
        grid.WithAgents(affineAgents.Concat(evenAgents).ToArray());

        await family.EvaluateAssignmentsAsync(grid);

        // The even store's agents may only land on the node capable of running them.
        foreach (var uri in evenAgents)
        {
            grid.AgentFor(uri).AssignedNode!.AssignedId.ShouldBe(1,
                "a blue/green agent must land on the node that declares it as a capability");
        }

        // The affine store is untouched by the other store's rollout: db1 whole on one node.
        affineAgents.Select(u => grid.AgentFor(u).AssignedNode).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task failover_reassigns_whole_groups_to_the_surviving_nodes()
    {
        var family = FamilyFor(StoreWith("main", "marten", DatabaseCardinality.StaticMultiple));

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        var node2 = grid.WithNode(2, Guid.NewGuid());

        var agents = new List<Uri>();
        foreach (var db in new[] { "db1", "db2" })
        foreach (var tenant in new[] { "t1", "t2" })
            agents.Add(Agent(db, tenant));

        grid.WithAgents(agents.ToArray());

        await family.EvaluateAssignmentsAsync(grid);
        grid.AllAgents.ShouldAllBe(a => a.AssignedNode != null);

        // Node 2 dies. Re-evaluating must land every group whole on a survivor.
        grid.Remove(node2);
        await family.EvaluateAssignmentsAsync(grid);

        foreach (var db in new[] { "db1", "db2" })
        {
            var assigned = new[] { "t1", "t2" }
                .Select(t => grid.AgentFor(Agent(db, t)).AssignedNode)
                .Distinct()
                .ToList();

            assigned.Count.ShouldBe(1, $"{db} must be whole on a surviving node");
            assigned[0]!.AssignedId.ShouldBe(1);
        }
    }
}

/// <summary>
/// JasperFx/jasperfx#486 (stale-agent retirement): when the supported agent set changes shape because a
/// database's tenant scoping changed — a database gains its first tenant (store-global agent replaced by
/// per-tenant agents) or loses one — an agent still running from the previous shape must be STOPPED by the
/// next assignment evaluation, or it double-processes the same events as its replacements.
/// </summary>
public class event_subscription_family_stale_agent_retirement
{
    private static readonly EventStoreIdentity TheIdentity = new("main", "marten");
    private static readonly DatabaseId TheDatabase = new("localhost", "claims1");
    private static readonly ShardName BaseShard = ShardName.Compose("provided-cares");

    private static EventStoreUsage UsageWith(params string[] tenantIds)
    {
        return new EventStoreUsage
        {
            Database = new DatabaseUsage
            {
                Databases =
                {
                    new DatabaseDescriptor
                    {
                        ServerName = TheDatabase.Server,
                        DatabaseName = TheDatabase.Name,
                        TenantIds = tenantIds.ToList()
                    }
                }
            },
            Subscriptions =
            {
                new SubscriptionDescriptor(SubscriptionType.SingleStreamProjection)
                {
                    Lifecycle = ProjectionLifecycle.Async,
                    ShardNames = new[] { BaseShard }
                }
            }
        };
    }

    private static EventSubscriptionAgentFamily FamilyFor(EventStoreUsage usage, bool distributesPerTenant = true)
    {
        var store = Substitute.For<IEventStore>();
        store.Identity.Returns(TheIdentity);
        store.DatabaseCardinality.Returns(DatabaseCardinality.StaticMultiple);
        store.DistributesAgentsPerTenant.Returns(distributesPerTenant);
        store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(Task.FromResult<EventStoreUsage?>(usage));
        return new EventSubscriptionAgentFamily(new[] { store }, Array.Empty<IObserver<ShardState>>());
    }

    private static Uri UriForTenant(string? tenantId)
        => EventSubscriptionAgentFamily.UriFor(TheIdentity, TheDatabase,
            tenantId == null ? BaseShard : BaseShard.ForTenant(tenantId));

    [Fact]
    public async Task running_store_global_agent_is_stopped_when_the_database_gains_its_first_tenants()
    {
        // The store now enumerates per-tenant agents (two tenants were provisioned at runtime) …
        var family = FamilyFor(UsageWith("t1", "t2"));

        var staleStoreGlobal = UriForTenant(null);

        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());

        // … but the store-global agent from the empty-database phase is still RUNNING on node 1.
        node1.Running(staleStoreGlobal);
        grid.WithAgents(UriForTenant("t1"), UriForTenant("t2"));

        await family.EvaluateAssignmentsAsync(grid);

        var stale = grid.AgentFor(staleStoreGlobal);
        stale.AssignedNode.ShouldBeNull("the superseded store-global agent must be detached so it is stopped");
        stale.OriginalNode.ShouldBe(node1, "the stop must target the node it was running on");

        // Its per-tenant replacements are assigned as one database group.
        var tenantNodes = new[] { "t1", "t2" }
            .Select(t => grid.AgentFor(UriForTenant(t)).AssignedNode)
            .ToList();
        tenantNodes.ShouldAllBe(n => n != null);
        tenantNodes.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task running_per_tenant_agent_is_stopped_when_its_tenant_is_removed()
    {
        // The inverse transition: tenant t2 was removed, so only t1 is enumerated …
        var family = FamilyFor(UsageWith("t1"));

        var staleTenantAgent = UriForTenant("t2");

        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());

        // … but t2's agent is still running from before the removal.
        node1.Running(staleTenantAgent);
        grid.WithAgents(UriForTenant("t1"));

        await family.EvaluateAssignmentsAsync(grid);

        grid.AgentFor(staleTenantAgent).AssignedNode.ShouldBeNull("the removed tenant's agent must be stopped");
        grid.AgentFor(UriForTenant("t1")).AssignedNode.ShouldNotBeNull();
    }

    [Fact]
    public async Task an_agent_with_no_supported_counterpart_is_left_alone()
    {
        // A running agent of a DIFFERENT projection (e.g. a green node's new projection the evaluating
        // store doesn't enumerate) shares no tenant-neutral key with the supported set, so retirement must
        // NOT touch it — that is the blue/green escape hatch.
        var family = FamilyFor(UsageWith("t1"));

        var unrelated = EventSubscriptionAgentFamily.UriFor(TheIdentity, TheDatabase,
            ShardName.Compose("brand-new-green-projection"));

        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());

        node1.Running(unrelated);
        grid.WithAgents(UriForTenant("t1"));

        await family.EvaluateAssignmentsAsync(grid);

        grid.AgentFor(unrelated).AssignedNode.ShouldNotBeNull("an agent without a superseding counterpart keeps running");
    }

    [Fact]
    public async Task a_different_version_of_the_same_shard_is_not_retired()
    {
        // Blue/green version bump: v2 is enumerated, v1 still runs on a blue node. The version slot —
        // not the tenant slot — differs, so v1 must keep running side by side during the rollout.
        var v2 = ShardName.Compose("provided-cares", version: 2);
        var usage = UsageWith("t1");
        usage.Subscriptions[0].ShardNames = new[] { v2 };
        var family = FamilyFor(usage);

        var v1Agent = EventSubscriptionAgentFamily.UriFor(TheIdentity, TheDatabase, BaseShard.ForTenant("t1"));

        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());

        node1.Running(v1Agent);

        await family.EvaluateAssignmentsAsync(grid);

        grid.AgentFor(v1Agent).AssignedNode.ShouldNotBeNull("a different projection version is a blue/green rollout, not supersession");
    }
}

public class tenant_neutral_key_of
{
    private static string? KeyOf(string uri) => EventSubscriptionAgentFamily.TenantNeutralKeyOf(new Uri(uri));

    [Fact]
    public void base_shard_and_its_tenant_scoped_variants_share_a_key()
    {
        var baseKey = KeyOf("event-subscriptions://marten/main/localhost.db1/proj/all");
        baseKey.ShouldNotBeNull();
        KeyOf("event-subscriptions://marten/main/localhost.db1/proj/all/t1").ShouldBe(baseKey);
        KeyOf("event-subscriptions://marten/main/localhost.db1/proj/all/t2").ShouldBe(baseKey);
    }

    [Fact]
    public void versions_do_not_share_a_key()
    {
        KeyOf("event-subscriptions://marten/main/localhost.db1/proj/all/v2")
            .ShouldNotBe(KeyOf("event-subscriptions://marten/main/localhost.db1/proj/all"));

        // … but a versioned base and its versioned tenant variant do.
        KeyOf("event-subscriptions://marten/main/localhost.db1/proj/all/v2/t1")
            .ShouldBe(KeyOf("event-subscriptions://marten/main/localhost.db1/proj/all/v2"));
    }

    [Fact]
    public void different_databases_projections_and_stores_do_not_share_a_key()
    {
        var key = KeyOf("event-subscriptions://marten/main/localhost.db1/proj/all/t1");

        KeyOf("event-subscriptions://marten/main/localhost.db2/proj/all/t1").ShouldNotBe(key);
        KeyOf("event-subscriptions://marten/main/localhost.db1/other/all/t1").ShouldNotBe(key);
        KeyOf("event-subscriptions://marten/other/localhost.db1/proj/all/t1").ShouldNotBe(key);
    }

    [Fact]
    public void malformed_uris_yield_no_key()
    {
        // Too short to carry a shard path at all.
        KeyOf("event-subscriptions://marten/main/localhost.db1").ShouldBeNull();

        // Six segments only fit the grammar as name/shardKey/v{n}/tenant.
        KeyOf("event-subscriptions://marten/main/localhost.db1/proj/all/nonversion/t1").ShouldBeNull();
    }

    [Fact]
    public void a_tenant_that_merely_looks_versionish_is_still_a_tenant_when_a_version_segment_precedes_it()
    {
        KeyOf("event-subscriptions://marten/main/localhost.db1/proj/all/v2/v3")
            .ShouldBe(KeyOf("event-subscriptions://marten/main/localhost.db1/proj/all/v2"));
    }
}
