using JasperFx.Core;
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
    public async Task find_all_known_agents()
    {
        var uris = await theProjectionAgents.AllKnownAgentsAsync();
        
        uris.Count.ShouldBe(3);
        uris.ShouldContain(ProjectionAgents.UriFor("Marten", "Day:All"));
        uris.ShouldContain(ProjectionAgents.UriFor("Marten", "Trip:All"));
        uris.ShouldContain(ProjectionAgents.UriFor("Marten", "Distance:All"));
    }

    [Fact]
    public async Task everything_is_started_up_on_one_node()
    {
        await theOriginalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());

        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = ProjectionAgents.SchemeName;
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
            w.AgentScheme = ProjectionAgents.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 1);
            w.ExpectRunningAgents(host2, 1);
            w.ExpectRunningAgents(host3, 1);
        }, 30.Seconds());
    }
}