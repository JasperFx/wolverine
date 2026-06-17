using System.Linq;
using MartenTests.Distribution.Support;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Marten.Distribution;
using Wolverine.Runtime.Agents;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

// Regression for #3124: IEventSubscriptionAgentFamily.FindAgentUriAsync must resolve the agent URI
// for a REGISTERED projection/subscription shard regardless of whether its agent is currently
// running, so operators can pause/restart/rebuild a stopped or not-yet-started projection.
public class find_agent_uri_for_registered_projection(ITestOutputHelper output) : SingleTenantContext(output)
{
    [Fact]
    public async Task resolves_registered_shard_without_a_running_agent()
    {
        var family = theOriginalHost.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();

        // "Trip:All" is the JasperFx ShardName.Identity for the registered TripProjection.
        var uri = await family.FindAgentUriAsync("Trip:All", null);

        uri.ShouldNotBeNull();
        uri!.AbsoluteUri.ShouldBe("event-subscriptions://marten/main/localhost.postgres/trip/all");
    }

    [Fact]
    public async Task returns_null_for_an_unregistered_shard()
    {
        var family = theOriginalHost.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();

        var uri = await family.FindAgentUriAsync("DoesNotExist:All", null);

        uri.ShouldBeNull();
    }
}
