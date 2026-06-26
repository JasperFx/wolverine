using IntegrationTests;
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

// GH-3163: rebuilding a projection that has NO live agent — an Inline or Live projection, or an async
// projection not currently distributed — must still work. Wolverine resolves the registered projection
// from the subscription set, spins up a transient rebuild agent for it on the handling node, runs the
// rebuild to the high-water mark, then stops it. (An Inline projection has no agent URI at all, so the
// usual "route the rebuild to the live agent" path can't reach it.)
public class inline_projection_rebuild : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("inline_rebuild");
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "inline_rebuild";

                        // Registered as INLINE — runs in the event-capture transaction, so it has NO
                        // continuous async agent and no distributed agent URI.
                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Inline);
                    })
                    .IntegrateWithWolverine(m => m.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        _host.GetRuntime().Agents.DisableHealthChecks();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task rebuild_a_registered_inline_projection_that_has_no_live_agent()
    {
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        var streamId = Guid.NewGuid();

        // The Inline projection writes the Trip read model in the same transaction as the events.
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Trip>(streamId, new TripStarted { Day = 1 }, new TripEnded { Day = 2 });
            await session.SaveChangesAsync();
        }

        await using (var session = store.QuerySession())
        {
            (await session.LoadAsync<Trip>(streamId)).ShouldNotBeNull("Inline projection should have created the Trip");
        }

        var family = _host.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();

        // An Inline projection is not distributed as an agent — there is no agent URI to route a rebuild
        // through. This is exactly the case the transient-rebuild path exists for.
        (await family.FindAgentUriAsync("Trip:All", null)).ShouldBeNull(
            "An Inline projection has no distributed agent, so it resolves no agent URI.");

        // Wipe the read model — only a genuine rebuild re-applies the events to restore it.
        await using (var session = store.LightweightSession())
        {
            session.DeleteWhere<Trip>(_ => true);
            await session.SaveChangesAsync();
        }
        await using (var session = store.QuerySession())
        {
            (await session.Query<Trip>().CountAsync()).ShouldBe(0);
        }

        // Rebuild via the transient-agent path: Wolverine finds the registered Inline projection, spins a
        // rebuild agent for it, replays to the high-water mark, then stops it.
        var rebuilt = await family.TryRebuildRegisteredProjectionAsync("Trip:All", null, CancellationToken.None);
        rebuilt.ShouldBeTrue("The registered Inline projection should be found and rebuilt.");

        // PROOF the rebuild ran: the Trip read model is restored from the event stream.
        await using (var session = store.QuerySession())
        {
            (await session.LoadAsync<Trip>(streamId)).ShouldNotBeNull(
                "The transient rebuild should have restored the Inline projection's read model.");
        }
    }

    [Fact]
    public async Task returns_false_for_an_unregistered_projection()
    {
        var family = _host.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();

        (await family.TryRebuildRegisteredProjectionAsync("DoesNotExist:All", null, CancellationToken.None))
            .ShouldBeFalse("An unregistered shard identity must report no match so the caller can try other families.");
    }
}
