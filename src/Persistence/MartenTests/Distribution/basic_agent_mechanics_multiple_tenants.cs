using JasperFx.Core;
using JasperFx.Events.Projections;
using Marten;
using MartenTests.Distribution.Support;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten.Distribution;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

public class basic_agent_mechanics_multiple_tenants(ITestOutputHelper output)
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
            // 3 projections x 2 databases = 6 total
            w.ExpectRunningAgents(theOriginalHost, 6);
        }, 60.Seconds());
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
            // 3 projections x 3 databases = 9 total
            w.ExpectRunningAgents(theOriginalHost, 9);
        }, 60.Seconds());
        await AssertAgentsUrisAsync(theOriginalHost, tenants);

        var extraHosts = await Task.WhenAll<IHost>([startHostAsync(), startHostAsync()]);
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            // 3 projections x 3 databases = 9 total
            w.ExpectRunningAgents(theOriginalHost, 3);
            w.ExpectRunningAgents(extraHosts[0], 3);
            w.ExpectRunningAgents(extraHosts[1], 3);
        }, 60.Seconds());
        await AssertAgentsUrisAsync(theOriginalHost, tenants);
        await AssertAgentsUrisAsync(extraHosts[0], tenants);
        await AssertAgentsUrisAsync(extraHosts[1], tenants);
    }

    private static async Task AssertAgentsUrisAsync(IHost host, params string[] tenants)
    {
        var uris = await GetAgentUrisAsync(host);
        var expectedUris = tenants.SelectMany(tenant => new[]
        {
            $"event-subscriptions://marten/main/localhost.{tenant}/trip/all",
            $"event-subscriptions://marten/main/localhost.{tenant}/day/all",
            $"event-subscriptions://marten/main/localhost.{tenant}/distance/all"
        });
        uris.ShouldBe(expectedUris, ignoreOrder: true);
    }

    protected override void SetupProjections(StoreOptions storeOptions)
    {
        storeOptions.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
        storeOptions.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
        storeOptions.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
    }
}