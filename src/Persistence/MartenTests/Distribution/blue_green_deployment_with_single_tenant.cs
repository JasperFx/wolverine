using JasperFx.Core;
using MartenTests.Distribution.Support;
using Shouldly;
using Wolverine;
using Wolverine.Marten.Distribution;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

public class blue_green_deployment_with_single_tenant(ITestOutputHelper output)
    : SingleTenantContext(output)
{
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

        var originalUris = await GetAgentUrisAsync(theOriginalHost);
        originalUris.ShouldBe([
            "event-subscriptions://marten/main/localhost.postgres/day/all",
            "event-subscriptions://marten/main/localhost.postgres/distance/all",
            "event-subscriptions://marten/main/localhost.postgres/trip/all"
        ], ignoreOrder: true);
        var greenUris = await GetAgentUrisAsync(greenHost);
        greenUris.ShouldBe([
            "event-subscriptions://marten/main/localhost.postgres/day/all",
            "event-subscriptions://marten/main/localhost.postgres/distance/all",
            "event-subscriptions://marten/main/localhost.postgres/ending/all",
            "event-subscriptions://marten/main/localhost.postgres/starting/all",
            "event-subscriptions://marten/main/localhost.postgres/trip/all/v2"
        ], ignoreOrder: true);
    }
}