using JasperFx.Core;
using JasperFx.Events.Projections;
using Marten;
using MartenTests.Distribution.Support;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten.Distribution;
using Wolverine.Runtime.Agents;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

// Regression for #3128: under database-per-tenant (sharded databases) FindAgentUriAsync must resolve
// a registered shard's agent URI for a specific tenant by mapping tenantId -> its database.
public class find_agent_uri_per_tenant_database(ITestOutputHelper output) : MultiTenantContext(output)
{
    protected override void SetupProjections(StoreOptions storeOptions)
    {
        storeOptions.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
        storeOptions.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
        storeOptions.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
    }

    [Fact]
    public async Task resolves_per_tenant_agent_uri()
    {
        string[] tenants = ["tenant1", "tenant2"];
        await AddTenantsAsync(tenants);

        // Wait until the tenant databases are picked up by the distribution (3 projections x 2 tenants)
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 6);
        }, 60.Seconds());

        var family = theOriginalHost.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();

        (await family.FindAgentUriAsync("Trip:All", "tenant1"))!.AbsoluteUri
            .ShouldBe("event-subscriptions://marten/main/localhost.tenant1/trip/all");

        (await family.FindAgentUriAsync("Day:All", "tenant2"))!.AbsoluteUri
            .ShouldBe("event-subscriptions://marten/main/localhost.tenant2/day/all");

        // An unknown tenant still resolves to nothing
        (await family.FindAgentUriAsync("Trip:All", "tenant-nope")).ShouldBeNull();
    }
}
