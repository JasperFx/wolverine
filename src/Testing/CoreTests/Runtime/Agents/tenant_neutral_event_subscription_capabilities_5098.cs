using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
///     GH-5098. A node captures its capabilities once at startup. Under per-tenant event partitioning a node that
///     started before a tenant was added still advertises the store-global agent (or the old tenants), and a node
///     that started after advertises the per-tenant agents. Matching on the exact URI made the two nodes look
///     like different releases, so the capability-matching (blue/green) path put every new tenant's agents on
///     the newer node only. Event subscription capabilities now compare tenant-neutrally: tenants are data, not
///     code. A new projection version is still a real capability difference.
/// </summary>
public class tenant_neutral_event_subscription_capabilities_5098
{
    private const string StoreWide = "event-subscriptions://marten/main/localhost.widgets/widgettally/all";

    private static Uri ForTenant(string tenant) => new($"{StoreWide}/{tenant}");

    private static readonly string[] Tenants = ["a", "b", "c", "d"];

    private static EventSubscriptionAgentFamily SingleDatabaseFamily()
    {
        var store = Substitute.For<IEventStore>();
        store.Identity.Returns(new EventStoreIdentity("main", "marten"));
        store.DatabaseCardinality.Returns(DatabaseCardinality.Single);
        store.StubUsage(null);

        return new EventSubscriptionAgentFamily([store], Array.Empty<IObserver<ShardState>>());
    }

    [Fact]
    public async Task new_tenant_agents_spread_over_a_node_that_started_before_the_tenants_existed()
    {
        // The CritterWatch #1431 node table: node 1 started with no tenants, node 2 after four were added and
        // is running all four of their agents
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid()).HasCapabilities([new Uri(StoreWide)]);
        grid.WithNode(2, Guid.NewGuid()).HasCapabilities(Tenants.Select(ForTenant))
            .Running(Tenants.Select(ForTenant).ToArray());

        grid.WithAgents(Tenants.Select(ForTenant).ToArray());

        await SingleDatabaseFamily().EvaluateAssignmentsAsync(grid);

        var perNode = Tenants.Select(t => grid.AgentFor(ForTenant(t)).AssignedNode!.AssignedId)
            .GroupBy(x => x)
            .OrderBy(x => x.Key)
            .Select(x => $"node {x.Key}: {x.Count()}")
            .ToArray();

        perNode.ShouldBe(["node 1: 2", "node 2: 2"]);
    }

    [Fact]
    public void a_store_wide_capability_declares_every_tenant_of_the_same_shard()
    {
        var grid = new AssignmentGrid();
        var node = grid.WithNode(1, Guid.NewGuid()).HasCapabilities([new Uri(StoreWide)]);

        node.TryAssign(ForTenant("a")).ShouldBeTrue();

        // A different projection, or a new version of this one, is still a different capability
        node.TryAssign(new Uri("event-subscriptions://marten/main/localhost.widgets/other/all/a")).ShouldBeFalse();
        node.TryAssign(new Uri($"{StoreWide}/v2/a")).ShouldBeFalse();
    }

    [Fact]
    public void different_tenant_sets_of_one_release_are_the_same_capabilities()
    {
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid()).HasCapabilities([ForTenant("a"), ForTenant("b")]);
        grid.WithNode(2, Guid.NewGuid()).HasCapabilities(Tenants.Select(ForTenant));
        grid.WithNode(3, Guid.NewGuid()).HasCapabilities([new Uri(StoreWide)]);

        grid.AllNodesHaveSameCapabilities(EventSubscriptionAgentFamily.SchemeName).ShouldBeTrue();
    }

    [Fact]
    public void a_new_projection_version_is_still_blue_green()
    {
        var grid = new AssignmentGrid();
        var blue = grid.WithNode(1, Guid.NewGuid()).HasCapabilities([ForTenant("a")]);
        var green = grid.WithNode(2, Guid.NewGuid()).HasCapabilities([new Uri($"{StoreWide}/v2/a")]);

        grid.AllNodesHaveSameCapabilities(EventSubscriptionAgentFamily.SchemeName).ShouldBeFalse();

        var v2 = new Uri($"{StoreWide}/v2/b");
        grid.WithAgents(v2, ForTenant("b"));
        grid.DistributeEvenlyWithBlueGreenSemantics(EventSubscriptionAgentFamily.SchemeName);

        grid.AgentFor(v2).AssignedNode.ShouldBe(green);
        grid.AgentFor(ForTenant("b")).AssignedNode.ShouldBe(blue);
    }

    [Fact]
    public void the_capability_key_is_only_for_event_subscription_agents()
    {
        EventSubscriptionAgentFamily.CapabilityKeyOf(ForTenant("a"))
            .ShouldBe(EventSubscriptionAgentFamily.CapabilityKeyOf(new Uri(StoreWide)));

        EventSubscriptionAgentFamily.CapabilityKeyOf(new Uri("wolverinedb://postgresql/localhost/db/main"))
            .ShouldBeNull();
    }
}
