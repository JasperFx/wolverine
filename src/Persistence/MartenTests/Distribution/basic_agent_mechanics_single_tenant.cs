using JasperFx.Core;
using Marten;
using MartenTests.Distribution.Support;
using Shouldly;
using Wolverine;
using Wolverine.Marten.Distribution;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

public class basic_agent_mechanics_single_tenant : SingleTenantContext
{
    public basic_agent_mechanics_single_tenant(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task can_do_the_full_marten_reset_all_data_call()
    {
        // It's a smoke test to fix GH-1057
        await theOriginalHost.ResetAllMartenDataAsync();
    }

    [Fact]
    public async Task find_all_known_agents()
    {
        var uris = await theProjectionAgents.AllKnownAgentsAsync();
        
        uris.Count.ShouldBe(3);
        
        uris.OrderBy(x => x.ToString()).ShouldBe([
            new Uri("event-subscriptions://marten/main/localhost.postgres/day/all"),
            new Uri("event-subscriptions://marten/main/localhost.postgres/distance/all"),
            new Uri("event-subscriptions://marten/main/localhost.postgres/trip/all"),
        
        ]);

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
    }

    [Fact]
    public async Task spread_out_over_multiple_hosts()
    {
        await theOriginalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        
        // Now, let's check that the load is redistributed!
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 1);
            w.ExpectRunningAgents(host2, 1);
            w.ExpectRunningAgents(host3, 1);
        }, 30.Seconds());
    }
}