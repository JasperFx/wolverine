using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// wolverine#3280: under sharded databases + per-tenant event partitioning multiple tenants are
/// co-located in one shard database, each drawing its own event sequence, so a single store-global agent
/// per (shard, database) cannot track them. When the store reports <see cref="IEventStore.DistributesAgentsPerTenant"/>,
/// <see cref="EventStoreAgents.SupportedAgentsAsync"/> must fan out one agent per (shard, tenant); for any
/// other store it must keep the single store-global agent.
/// </summary>
public class event_store_agents_per_tenant_distribution
{
    private static EventStoreUsage UsageWithOneAsyncShardOnADatabaseWith(params string[] tenantIds)
    {
        return new EventStoreUsage
        {
            Database = new DatabaseUsage
            {
                Databases =
                {
                    new DatabaseDescriptor
                    {
                        ServerName = "localhost",
                        DatabaseName = "claims1",
                        TenantIds = tenantIds.ToList()
                    }
                }
            },
            Subscriptions =
            {
                new SubscriptionDescriptor(SubscriptionType.SingleStreamProjection)
                {
                    Lifecycle = ProjectionLifecycle.Async,
                    ShardNames = new[] { ShardName.Compose("provided-cares") }
                }
            }
        };
    }

    private static EventStoreAgents AgentsFor(EventStoreUsage usage, bool distributesPerTenant)
    {
        var store = Substitute.For<IEventStore>();
        store.Identity.Returns(new EventStoreIdentity("main", "marten"));
        store.DistributesAgentsPerTenant.Returns(distributesPerTenant);
        store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(Task.FromResult<EventStoreUsage?>(usage));
        return new EventStoreAgents(store, Array.Empty<IObserver<ShardState>>());
    }

    [Fact]
    public async Task fans_out_one_agent_per_tenant_when_the_store_distributes_per_tenant()
    {
        var agents = AgentsFor(UsageWithOneAsyncShardOnADatabaseWith("01059910", "08004464"), distributesPerTenant: true);

        var uris = await agents.SupportedAgentsAsync(CancellationToken.None);

        uris.Count.ShouldBe(2, "one async shard on a two-tenant database must yield one agent per tenant");
        uris.ShouldContain(u => u.AbsolutePath.TrimEnd('/').EndsWith("/01059910", StringComparison.OrdinalIgnoreCase));
        uris.ShouldContain(u => u.AbsolutePath.TrimEnd('/').EndsWith("/08004464", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task keeps_a_single_store_global_agent_when_the_store_does_not_distribute_per_tenant()
    {
        var agents = AgentsFor(UsageWithOneAsyncShardOnADatabaseWith("01059910", "08004464"), distributesPerTenant: false);

        var uris = await agents.SupportedAgentsAsync(CancellationToken.None);

        uris.Count.ShouldBe(1, "without per-tenant distribution a shard keeps its single store-global agent");
        uris.ShouldNotContain(u => u.AbsolutePath.TrimEnd('/').EndsWith("/01059910", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task keeps_a_single_agent_when_the_database_has_no_tenants_yet()
    {
        var agents = AgentsFor(UsageWithOneAsyncShardOnADatabaseWith(), distributesPerTenant: true);

        var uris = await agents.SupportedAgentsAsync(CancellationToken.None);

        uris.Count.ShouldBe(1, "a per-tenant store with no tenants provisioned yet keeps one store-global agent");
    }

    [Fact]
    public async Task dedupes_tenants_case_insensitively_and_skips_blank_ids()
    {
        // Tenant ids are matched case-insensitively elsewhere (TryResolveTenantDatabaseIdAsync), so the
        // fan-out must not produce two agents for the same logical tenant in different casing, nor an
        // invalid agent for a blank id that slipped into the usage descriptor.
        var agents = AgentsFor(
            UsageWithOneAsyncShardOnADatabaseWith("Clinic-A", "clinic-a", "clinic-b", "", "   "),
            distributesPerTenant: true);

        var uris = await agents.SupportedAgentsAsync(CancellationToken.None);

        uris.Count.ShouldBe(2, "one agent per logical tenant: clinic-a (deduped across casing) and clinic-b");
        uris.ShouldContain(u => u.AbsolutePath.TrimEnd('/').EndsWith("/clinic-a", StringComparison.OrdinalIgnoreCase));
        uris.ShouldContain(u => u.AbsolutePath.TrimEnd('/').EndsWith("/clinic-b", StringComparison.OrdinalIgnoreCase));
    }
}
