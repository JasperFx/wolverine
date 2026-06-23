using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Polecat;
using Wolverine.Polecat.Publishing;
using Wolverine.Tracking;

namespace PolecatTests.AncillaryStores;

// GH-3109: Polecat mirror of bootstrapping_ancillary_marten_stores_with_wolverine. Brings up a primary
// (SQL-Server-backed) Polecat store integrated with Wolverine plus an ancillary Polecat store
// (AddPolecatStore<IPlayerStore>().IntegrateWithWolverine()), and verifies the ancillary integration is
// fully wired: AncillaryMessageStore + OutboxedSessionFactory<T> registered, envelope schema built, and a
// [PolecatStore]-routed handler opens/commits through the ancillary store end to end.
public class bootstrapping_ancillary_polecat_stores_with_wolverine : IAsyncLifetime
{
    private IHost theHost = null!;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // IMPORTANT FOR MODULAR MONOLITH USAGE: share one envelope storage schema across modules.
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                // Primary Polecat store + Wolverine integration (SQL Server backed).
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "anc_main";
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                // Ancillary Polecat store on the same SQL Server, different document schema.
                opts.Services.AddPolecatStore<IPlayerStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = "players";
                    })
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PlayerMessageHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public void registers_the_ancillary_store()
    {
        theHost.Services.GetRequiredService<IPlayerStore>().ShouldNotBeNull();

        var ancillaries = theHost.Services.GetServices<AncillaryMessageStore>();
        ancillaries.Any(x => x.MarkerType == typeof(IPlayerStore)).ShouldBeTrue();
    }

    [Fact]
    public void registers_the_outbox_factory_for_the_store()
    {
        theHost.Services.GetRequiredService<OutboxedSessionFactory<IPlayerStore>>()
            .ShouldNotBeNull();
    }

    [Fact]
    public void resolves_both_the_primary_and_ancillary_event_stores()
    {
        // GH-3219: ancillary Polecat stores must surface via GetServices<IEventStore>() the same way the
        // primary does (and the same way core Marten registers both) — otherwise the ancillary store is
        // invisible to the read-only capabilities / CritterWatch projection-explorer surface that
        // discovers stores this way. Asserted by reference because both stores are present as distinct
        // IEventStore instances (Polecat currently reports the same Identity string for both, which is a
        // separate concern from discovery).
        var stores = theHost.Services.GetServices<IEventStore>().ToArray();

        var primary = (IEventStore)theHost.Services.GetRequiredService<IDocumentStore>();
        var ancillary = (IEventStore)theHost.Services.GetRequiredService<IPlayerStore>();

        primary.ShouldNotBeSameAs(ancillary);
        stores.ShouldContain(primary);
        stores.ShouldContain(ancillary);
    }

    [Fact]
    public async Task try_to_use_the_session_transactional_middleware_end_to_end()
    {
        var message = new PlayerMessage(Guid.NewGuid().ToString());
        await theHost.InvokeMessageAndWaitAsync(message);

        // The [PolecatStore]-routed handler must have stored the Player in the ANCILLARY store.
        var store = theHost.Services.GetRequiredService<IPlayerStore>();
        await using var session = store.QuerySession();
        var player = await session.LoadAsync<Player>(message.Id);

        player.ShouldNotBeNull();
    }
}

public record PlayerMessage(string Id);

#region sample_polecat_store_handler
// This will use a Polecat session from the IPlayerStore ancillary store rather than the
// main IDocumentStore.
[PolecatStore(typeof(IPlayerStore))]
public static class PlayerMessageHandler
{
    public static void Handle(PlayerMessage message, IDocumentSession session)
    {
        session.Store(new Player { Id = message.Id });
    }
}

#endregion
