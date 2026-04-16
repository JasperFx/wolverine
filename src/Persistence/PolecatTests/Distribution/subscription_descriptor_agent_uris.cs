using IntegrationTests;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Polecat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PolecatTests.Distribution.TripDomain;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.Polecat.Distribution;

namespace PolecatTests.Distribution;

public class subscription_descriptor_agent_uris
{
    /// <summary>
    /// Ensures the EventStoreUsage has Database populated.
    /// Polecat 1.0 doesn't populate this; Polecat 1.1+ will.
    /// This helper bridges the gap for testing.
    /// </summary>
    private static void EnsureDatabasePopulated(EventStoreUsage usage, IDocumentStore store)
    {
        if (usage.Database != null) return;

        var documentStore = (DocumentStore)store;
        usage.Database = new DatabaseUsage
        {
            Cardinality = DatabaseCardinality.Single,
            MainDatabase = documentStore.Database.Describe()
        };
    }

    [Fact]
    public async Task agent_uris_match_event_subscription_family_uris()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddPolecat(opts =>
                {
                    opts.ConnectionString = Servers.SqlServerConnectionString;
                    opts.DatabaseSchemaName = "agent_uri_test";

                    opts.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                    opts.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                    opts.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
                });
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        var eventStore = (IEventStore)store;
        var usage = await eventStore.TryCreateUsage(CancellationToken.None);

        usage.ShouldNotBeNull();

        // Bridge for Polecat 1.0 — can be removed after upgrading to 1.1+
        EnsureDatabasePopulated(usage, store);

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

                // Should contain the store identity (URI host is case-insensitive)
                uri.Host.ShouldBe(eventStore.Identity.Type, StringCompareShould.IgnoreCase);
                uri.Segments[1].Trim('/').ShouldBe(eventStore.Identity.Name, StringCompareShould.IgnoreCase);
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
                services.AddPolecat(opts =>
                {
                    opts.ConnectionString = Servers.SqlServerConnectionString;
                    opts.DatabaseSchemaName = "agent_uri_inline_test";

                    opts.Projections.Add<TripProjection>(ProjectionLifecycle.Inline);
                });
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        var eventStore = (IEventStore)store;
        var usage = await eventStore.TryCreateUsage(CancellationToken.None);

        usage.ShouldNotBeNull();

        // Bridge for Polecat 1.0
        EnsureDatabasePopulated(usage, store);

        usage.PopulateAgentUris(ServiceCapabilities.EventSubscriptionAgentScheme, eventStore.Identity);

        foreach (var subscription in usage.Subscriptions)
        {
            subscription.AgentUris.ShouldBeEmpty();
        }
    }
}
