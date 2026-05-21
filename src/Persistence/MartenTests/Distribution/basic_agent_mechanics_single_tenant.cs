using JasperFx.Core;
using Marten;
using MartenTests.Distribution.Support;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten.Distribution;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

public class basic_agent_mechanics_single_tenant(ITestOutputHelper output)
    : SingleTenantContext(output)
{
    [Fact]
    public async Task can_do_the_full_marten_reset_all_data_call()
    {
        // It's a smoke test to fix GH-1057
        await theOriginalHost.ResetAllMartenDataAsync();
    }

    [Fact]
    public async Task everything_is_started_up_on_one_node()
    {
        await theOriginalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());

        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 3);
        }, 30.Seconds());
        await AssertAgentUrisAsync(theOriginalHost);
    }

    [Fact]
    public async Task spread_out_over_multiple_hosts()
    {
        await theOriginalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var hosts = await Task.WhenAll<IHost>([startHostAsync(), startHostAsync()]);
        // Now, let's check that the load is redistributed!
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 1);
            w.ExpectRunningAgents(hosts[0], 1);
            w.ExpectRunningAgents(hosts[1], 1);
        }, 30.Seconds());
        await AssertAgentUrisAsync(theOriginalHost);
        await AssertAgentUrisAsync(hosts[0]);
        await AssertAgentUrisAsync(hosts[1]);
    }

    private async static Task AssertAgentUrisAsync(IHost host)
    {
        var uris = await GetAgentUrisAsync(host);
        uris.ShouldBe([
            "event-subscriptions://marten/main/localhost.postgres/day/all",
            "event-subscriptions://marten/main/localhost.postgres/distance/all",
            "event-subscriptions://marten/main/localhost.postgres/trip/all",
        ], ignoreOrder: true);
    }
}