using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.Marten.Distribution;

namespace MartenTests.Distribution;

public class subscription_descriptor_agent_uris
{
    [Fact]
    public async Task agent_uris_match_event_subscription_family_uris()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddMarten(opts =>
                {
                    opts.Connection(Servers.PostgresConnectionString);
                    opts.DatabaseSchemaName = "agent_uri_test";
                    opts.DisableNpgsqlLogging = true;

                    opts.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                    opts.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                    opts.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
                });
            }).StartAsync();

        var eventStore = host.Services.GetServices<IEventStore>().Single();
        var usage = await eventStore.TryCreateUsage(CancellationToken.None);

        usage.ShouldNotBeNull();

        // Populate agent URIs using the same scheme as EventSubscriptionAgentFamily
        usage.PopulateAgentUris(ServiceCapabilities.EventSubscriptionAgentScheme, eventStore.Identity);

        // Verify async subscriptions have agent URIs
        var asyncSubscriptions = usage.Subscriptions
            .Where(x => x.Lifecycle == ProjectionLifecycle.Async)
            .ToArray();

        asyncSubscriptions.Length.ShouldBe(3);

        foreach (var subscription in asyncSubscriptions)
        {
            subscription.AgentUris.ShouldNotBeEmpty();

            // Verify each agent URI matches the EventSubscriptionAgentFamily pattern
            foreach (var agentUri in subscription.AgentUris)
            {
                var uri = new Uri(agentUri);
                uri.Scheme.ShouldBe(EventSubscriptionAgentFamily.SchemeName);

                // Should contain the store identity
                uri.Host.ShouldBe(eventStore.Identity.Type);
                uri.Segments[1].Trim('/').ShouldBe(eventStore.Identity.Name);
            }
        }

        // Verify the total count of agent URIs across all subscriptions
        // matches what EventSubscriptionAgentFamily would compute
        var allAgentUris = asyncSubscriptions
            .SelectMany(x => x.AgentUris)
            .OrderBy(x => x)
            .ToArray();

        // With single database and 3 async projections (each with one shard),
        // we expect 3 agent URIs total
        allAgentUris.Length.ShouldBe(3);
    }

    [Fact]
    public async Task agent_uris_are_empty_for_inline_projections()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddMarten(opts =>
                {
                    opts.Connection(Servers.PostgresConnectionString);
                    opts.DatabaseSchemaName = "agent_uri_inline_test";
                    opts.DisableNpgsqlLogging = true;

                    opts.Projections.Add<TripProjection>(ProjectionLifecycle.Inline);
                });
            }).StartAsync();

        var eventStore = host.Services.GetServices<IEventStore>().Single();
        var usage = await eventStore.TryCreateUsage(CancellationToken.None);

        usage.ShouldNotBeNull();

        usage.PopulateAgentUris(ServiceCapabilities.EventSubscriptionAgentScheme, eventStore.Identity);

        foreach (var subscription in usage.Subscriptions)
        {
            subscription.AgentUris.ShouldBeEmpty();
        }
    }
}
