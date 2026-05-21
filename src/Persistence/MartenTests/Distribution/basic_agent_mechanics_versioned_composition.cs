using JasperFx.Core;
using Marten;
using MartenTests.Distribution.Support;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten.Distribution;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

public class basic_agent_mechanics_versioned_composition(ITestOutputHelper output)
    : MultiTenantContext(output)
{
    [Fact]
    public async Task start_with_multiple_databases_on_one_single_node()
    {
        string[] tenants = ["tenant1", "tenant2"];
        await AddTenantsAsync(tenants);

        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            // 1 composite projection x 2 databases = 2 total
            w.ExpectRunningAgents(theOriginalHost, 2);
        }, 30.Seconds());
        await AssertAgentsUrisAsync(theOriginalHost, tenants);
    }

    [Fact]
    public async Task spread_databases_out_via_host()
    {
        string[] tenants = ["tenant1", "tenant2", "tenant3"];
        await AddTenantsAsync(tenants);

        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            // 1 composite projection x 3 databases = 3 total
            w.ExpectRunningAgents(theOriginalHost, 3);
        }, 30.Seconds());
        await AssertAgentsUrisAsync(theOriginalHost, tenants);

        var extraHosts = await Task.WhenAll<IHost>([startHostAsync(), startHostAsync()]);
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            // 1 composite projection x 3 databases = 3 total
            w.ExpectRunningAgents(theOriginalHost, 1);
            w.ExpectRunningAgents(extraHosts[0], 1);
            w.ExpectRunningAgents(extraHosts[1], 1);
        }, 30.Seconds());
        await AssertAgentsUrisAsync(theOriginalHost, tenants);
        await AssertAgentsUrisAsync(extraHosts[0], tenants);
        await AssertAgentsUrisAsync(extraHosts[1], tenants);
    }

    private static async Task AssertAgentsUrisAsync(IHost host, params string[] tenants)
    {
        var uris = await GetAgentUrisAsync(host);
        var expectedUris = tenants.Select(tenant =>
            $"event-subscriptions://marten/main/localhost.{tenant}/trips/all/v2");
        uris.ShouldBe(expectedUris, ignoreOrder: true);
    }

    protected override void SetupProjections(StoreOptions storeOptions)
    {
        storeOptions.Projections.CompositeProjectionFor("Trips", x =>
        {
            x.Version = 2;
            x.Add<TripProjection>();
            x.Add<DayProjection>();
            x.Add<DistanceProjection>();
        });
    }
}
