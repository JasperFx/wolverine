using IntegrationTests;
using JasperFx.Resources;
using Marten;
using MartenTests.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

public class ancillary_stores_use_different_databases : IAsyncLifetime
{
    private IHost theHost;

    private string playersConnectionString;
    private string thingsConnectionString;
    private DurabilityAgentFamily theFamily;
    
    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("players");

        playersConnectionString = await CreateDatabaseIfNotExists(conn, "players");
        thingsConnectionString = await CreateDatabaseIfNotExists(conn, "things");
        
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";

                opts.Services.AddMarten(Servers.PostgresConnectionString).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMartenStore<IPlayerStore>(m =>
                    {
                        m.Connection(playersConnectionString);
                    })
                    .IntegrateWithWolverine()

                    // Add a subscription
                    .SubscribeToEvents(new ColorsSubscription())

                    // Forward events to wolverine handlers
                    .PublishEventsToWolverine("PlayerEvents", x => { x.PublishEvent<ColorsUpdated>(); });

                // Look at that, it even works with Marten multi-tenancy through separate databases!
                opts.Services.AddMartenStore<IThingStore>(m =>
                {
                    m.Connection(thingsConnectionString);
                }).IntegrateWithWolverine(masterDatabaseConnectionString: Servers.PostgresConnectionString);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
        
        theFamily = new DurabilityAgentFamily(theHost.GetRuntime());
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<string> CreateDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        var exists = await conn.DatabaseExists(databaseName);
        if (!exists)
        {
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);
        }

        builder.Database = databaseName;

        return builder.ConnectionString;
    }

    [Fact]
    public async Task create_transaction_for_separate_store_on_different_database()
    {
        var factory = theHost.Services.GetRequiredService<OutboxedSessionFactory<IPlayerStore>>();
        var context = new MessageContext(theHost.GetRuntime());

        await using var session = factory.OpenSession(context);
        
        context.Storage.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost/players/wolverine"));

        var builder = new NpgsqlConnectionStringBuilder(session.Connection.ConnectionString);
        builder.Database.ShouldBe("players");
    }

    [Fact]
    public async Task have_durability_agents_for_other_databases()
    {
        var uris = await theFamily.AllKnownAgentsAsync();
        uris.ShouldBe([
            new Uri("wolverinedb://postgresql/localhost/postgres/wolverine"),
            new Uri("wolverinedb://postgresql/localhost/players/wolverine"),
            new Uri("wolverinedb://postgresql/localhost/things/wolverine"),
        ]);
    }
}