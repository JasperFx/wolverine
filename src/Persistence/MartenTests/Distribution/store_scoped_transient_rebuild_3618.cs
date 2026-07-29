using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests.Distribution;

// GH-3618: the last-resort transient-rebuild path (GH-3163) took no store argument and walked every store
// the family manages, returning on the first registered shard that matched by NAME. When the same projection
// name is registered in more than one store AND neither has a live agent -- both Inline here -- the caller
// could not steer the rebuild to the store the operator actually asked about; whichever store happened to be
// enumerated first won. The store-aware overload closes that gap.
public class store_scoped_transient_rebuild_3618 : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _main = null!;
    private IGh3618AncillaryStore _ancillary = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("gh3618_main");
            await conn.DropSchemaAsync("gh3618_anc");
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // TripProjection is registered under the SAME name in BOTH stores, and Inline in both, so
                // neither has a distributed agent to route a rebuild through. This is the ambiguity.
                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "gh3618_main";
                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Inline);
                    })
                    .IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Services.AddMartenStore<IGh3618AncillaryStore>(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "gh3618_anc";
                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Inline);
                    })
                    .IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();

        _main = _host.Services.GetRequiredService<IDocumentStore>();
        _ancillary = _host.Services.GetRequiredService<IGh3618AncillaryStore>();
    }

    public async ValueTask DisposeAsync()
    {
        _host.GetRuntime().Agents.DisableHealthChecks();
        await _host.StopAsync();
        _host.Dispose();
    }

    private EventSubscriptionAgentFamily theFamily =>
        _host.Services.GetServices<IEventSubscriptionAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();

    private static string identityOf(IDocumentStore store) => ((IEventStore)store).Identity.ToString();

    private static async Task<Guid> writeATripAsync(IDocumentStore store)
    {
        var streamId = Guid.NewGuid();
        await using var session = store.LightweightSession();
        session.Events.StartStream<Trip>(streamId, new TripStarted { Day = 1 }, new TripEnded { Day = 2 });
        await session.SaveChangesAsync();
        return streamId;
    }

    private static async Task wipeTheReadModelAsync(IDocumentStore store)
    {
        await using var session = store.LightweightSession();
        session.DeleteWhere<Trip>(_ => true);
        await session.SaveChangesAsync();
    }

    private static async Task<int> tripCountAsync(IDocumentStore store)
    {
        await using var session = store.QuerySession();
        return await session.Query<Trip>().CountAsync();
    }

    [Fact]
    public void both_stores_are_managed_by_the_one_family()
    {
        // Precondition for the ambiguity to exist at all.
        identityOf(_main).ShouldNotBe(identityOf(_ancillary));
    }

    [Fact]
    public async Task neither_store_has_a_live_agent_for_the_inline_projection()
    {
        // Establishes that this is the no-live-agent path, not the store-scoped live-agent path.
        (await theFamily.FindAgentUriAsync("Trip:All", null)).ShouldBeNull();
    }

    [Fact]
    public async Task rebuilding_the_main_store_leaves_the_ancillary_read_model_alone()
    {
        await writeATripAsync(_main);
        await writeATripAsync(_ancillary);

        await wipeTheReadModelAsync(_main);
        await wipeTheReadModelAsync(_ancillary);

        (await theFamily.TryRebuildRegisteredProjectionAsync(identityOf(_main), "Trip:All", null,
            CancellationToken.None)).ShouldBeTrue();

        (await tripCountAsync(_main)).ShouldBe(1, "the store named in the rebuild should have been rebuilt");
        (await tripCountAsync(_ancillary)).ShouldBe(0, "a store that was NOT named must be left untouched");
    }

    [Fact]
    public async Task rebuilding_the_ancillary_store_leaves_the_main_read_model_alone()
    {
        // The mirror image. Asserting both directions is what proves the overload narrows by store rather
        // than just happening to agree with the family's enumeration order.
        await writeATripAsync(_main);
        await writeATripAsync(_ancillary);

        await wipeTheReadModelAsync(_main);
        await wipeTheReadModelAsync(_ancillary);

        (await theFamily.TryRebuildRegisteredProjectionAsync(identityOf(_ancillary), "Trip:All", null,
            CancellationToken.None)).ShouldBeTrue();

        (await tripCountAsync(_ancillary)).ShouldBe(1, "the store named in the rebuild should have been rebuilt");
        (await tripCountAsync(_main)).ShouldBe(0, "a store that was NOT named must be left untouched");
    }

    [Fact]
    public async Task an_unregistered_shard_in_a_known_store_reports_no_match()
    {
        (await theFamily.TryRebuildRegisteredProjectionAsync(identityOf(_main), "DoesNotExist:All", null,
            CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task a_store_outside_this_family_reports_no_match()
    {
        (await theFamily.TryRebuildRegisteredProjectionAsync("someone-else:Marten", "Trip:All", null,
            CancellationToken.None)).ShouldBeFalse(
            "so a caller looping over families can try the next one");
    }

    /// <summary>
    ///     Documents the gap itself. The store-blind overload is unchanged and still reports success, but it
    ///     stops at the FIRST store with a matching registered shard -- so with the projection name shared
    ///     across both stores it rebuilds exactly one of them, and the caller has no say in which. That is
    ///     the whole reason the store-aware overload exists.
    /// </summary>
    [Fact]
    public async Task the_store_blind_overload_rebuilds_only_one_of_the_two_stores()
    {
        await writeATripAsync(_main);
        await writeATripAsync(_ancillary);

        await wipeTheReadModelAsync(_main);
        await wipeTheReadModelAsync(_ancillary);

        (await theFamily.TryRebuildRegisteredProjectionAsync("Trip:All", null, CancellationToken.None))
            .ShouldBeTrue();

        var rebuilt = await tripCountAsync(_main) + await tripCountAsync(_ancillary);
        rebuilt.ShouldBe(1,
            "the store-blind overload returns on the first matching store, so it cannot cover both -- " +
            "which is exactly why a caller needs to name the store it means");
    }
}

public interface IGh3618AncillaryStore : IDocumentStore;
