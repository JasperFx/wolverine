using IntegrationTests;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Polecat;
using Wolverine.SqlServer.Persistence;
using Wolverine.Tracking;

namespace PolecatTests.AncillaryStores;

// GH-3109 (Polecat mirror of MartenTests' ancillary_store_subject_uri_uniqueness): when multiple
// Polecat stores live in the same host, each store's Wolverine message-store identity must be unique
// so JasperFx generates separate schema migrations / durability agents per store. On SQL Server the
// per-store identity is the agent Uri (engine/server/database/envelope-schema) rather than the
// SubjectUri (which the RDBMS store only specializes for the Main role).
public class ancillary_store_subject_uri_uniqueness : IAsyncLifetime
{
    private IHost theHost = null!;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Primary Polecat store.
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "uri_main";
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                // Ancillary store 1 — same database, different envelope schema.
                opts.Services.AddPolecatStore<IPlayerStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = "uri_players";
                    })
                    .IntegrateWithWolverine(x => x.SchemaName = "uri_players_wolverine");

                // Ancillary store 2 — same database, different envelope schema.
                opts.Services.AddPolecatStore<IThingStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = "uri_things";
                    })
                    .IntegrateWithWolverine(x => x.SchemaName = "uri_things_wolverine");

                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public void every_store_should_have_a_unique_identity_uri()
    {
        var runtime = theHost.GetRuntime();

        var primaryUri = runtime.Stores.Main.As<SqlServerMessageStore>().Uri;

        var ancillaries = theHost.Services.GetServices<AncillaryMessageStore>().ToList();

        var playerUri = ancillaries.Single(x => x.MarkerType == typeof(IPlayerStore))
            .Inner.As<SqlServerMessageStore>().Uri;

        var thingUri = ancillaries.Single(x => x.MarkerType == typeof(IThingStore))
            .Inner.As<SqlServerMessageStore>().Uri;

        // All three must be distinct so JasperFx generates separate migration scripts / durability
        // agents per store.
        playerUri.ShouldNotBe(primaryUri);
        thingUri.ShouldNotBe(primaryUri);
        thingUri.ShouldNotBe(playerUri);
    }
}
