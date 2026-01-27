using JasperFx.Core;
using MartenTests.Distribution.Support;
using Wolverine;
using Wolverine.Marten.Distribution;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

public class blue_green_deployment_with_single_tenant : SingleTenantContext
{
    public blue_green_deployment_with_single_tenant(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task spin_up_single_blue_and_single_green_host()
    {
        var greenHost = await startGreenHostAsync();

        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 3);
            w.ExpectRunningAgents(greenHost, 3);
        }, 30.Seconds());
        
        // TODO -- tighten the assertions here!
    }
}