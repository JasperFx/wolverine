using IntegrationTests;
using JasperFx.Core;
using MartenTests.Distribution.Support;
using Shouldly;
using Wolverine;
using Wolverine.Marten.Distribution;
using Wolverine.Runtime.Agents;
using Xunit;
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

        var db = Servers.PostgresDatabaseName;
        var originalUris = await GetAgentUrisAsync(theOriginalHost);
        originalUris.ShouldBe([
            $"event-subscriptions://marten/main/localhost.{db}/day/all",
            $"event-subscriptions://marten/main/localhost.{db}/distance/all",
            $"event-subscriptions://marten/main/localhost.{db}/trip/all"
        ], ignoreOrder: true);
        var greenUris = await GetAgentUrisAsync(greenHost);
        greenUris.ShouldBe([
            $"event-subscriptions://marten/main/localhost.{db}/day/all",
            $"event-subscriptions://marten/main/localhost.{db}/distance/all",
            $"event-subscriptions://marten/main/localhost.{db}/ending/all",
            $"event-subscriptions://marten/main/localhost.{db}/starting/all",
            $"event-subscriptions://marten/main/localhost.{db}/trip/all/v2"
        ], ignoreOrder: true);
    }
}