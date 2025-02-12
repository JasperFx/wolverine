using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using MartenTests.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Marten.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace MartenTests.AncillaryStores;

public class bootstrapping_ancillary_marten_stores_with_wolverine : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private string tenant1ConnectionString;
    private string tenant2ConnectionString;
    private string tenant3ConnectionString;
    private DurabilityAgentFamily theFamily;
    private IHost theHost;

    public bootstrapping_ancillary_marten_stores_with_wolverine(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("players");

        tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant1");
        tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant2");
        tenant3ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant3");

        await dropSchemaOnDatabase(tenant1ConnectionString, "things");
        await dropSchemaOnDatabase(tenant2ConnectionString, "things");
        await dropSchemaOnDatabase(tenant3ConnectionString, "things");

        #region sample_bootstrapping_with_ancillary_marten_stores

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";

                opts.Services.AddMarten(Servers.PostgresConnectionString).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMartenStore<IPlayerStore>(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "players";
                    })
                    .IntegrateWithWolverine()

                    // Add a subscription
                    .SubscribeToEvents(new ColorsSubscription())

                    // Forward events to wolverine handlers
                    .PublishEventsToWolverine("PlayerEvents", x => { x.PublishEvent<ColorsUpdated>(); });

                // Look at that, it even works with Marten multi-tenancy through separate databases!
                opts.Services.AddMartenStore<IThingStore>(m =>
                {
                    m.MultiTenantedDatabases(tenancy =>
                    {
                        tenancy.AddSingleTenantDatabase(tenant1ConnectionString, "tenant1");
                        tenancy.AddSingleTenantDatabase(tenant2ConnectionString, "tenant2");
                        tenancy.AddSingleTenantDatabase(tenant3ConnectionString, "tenant3");
                    });
                    m.DatabaseSchemaName = "things";
                }).IntegrateWithWolverine(masterDatabaseConnectionString: Servers.PostgresConnectionString);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        #endregion

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

    private async Task dropSchemaOnDatabase(string connectionString, string schemaName)
    {
        using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(schemaName);
        await conn.CloseAsync();
    }

    [Fact]
    public void registers_the_single_tenant_ancillary_store()
    {
        theHost.DocumentStore<IPlayerStore>().ShouldNotBeNull();
        var ancillaries = theHost.Services.GetServices<IAncillaryMessageStore>();
        ancillaries.OfType<PostgresqlMessageStore<IPlayerStore>>().Any().ShouldBeTrue();
    }

    [Fact]
    public void registers_the_multiple_tenant_ancillary_store()
    {
        theHost.DocumentStore<IThingStore>().ShouldNotBeNull();
        var ancillaries = theHost.Services.GetServices<IAncillaryMessageStore>();
        ancillaries.OfType<MultiTenantedMessageStore<IThingStore>>().Any()
            .ShouldBeTrue();
    }

    [Fact]
    public void registers_the_outbox_factory_for_the_store()
    {
        theHost.Services.GetRequiredService<OutboxedSessionFactory<IPlayerStore>>()
            .ShouldNotBeNull();

        theHost.Services.GetRequiredService<OutboxedSessionFactory<IThingStore>>()
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task can_discover_all_the_databases()
    {
        var databaseSources = theHost.Services.GetServices<IDatabaseSource>();
        var source = databaseSources
            .OfType<MartenMessageDatabaseDiscovery>()
            .Single();

        var databases = await source.BuildDatabases();

        // 3 tenant databases + 1 master database
        databases.OfType<PostgresqlMessageStore>().Select(x => x.Name)
            .OrderBy(x => x)
            .ShouldHaveTheSameElementsAs("default", "tenant1", "tenant2", "tenant3");
    }

    [Fact]
    public async Task builds_out_envelope_schema()
    {
        await assertTablesExist(Servers.PostgresConnectionString, "wolverine");
        await assertTablesExist(tenant1ConnectionString, "wolverine");
        await assertTablesExist(tenant2ConnectionString, "wolverine");
        await assertTablesExist(tenant3ConnectionString, "wolverine");
    }

    private async Task assertTablesExist(string connectionString, string schemaName)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var tables = await conn.ExistingTablesAsync(schemas: [schemaName]);
        tables.ShouldContain(new DbObjectName(schemaName, DatabaseConstants.IncomingTable));
        tables.ShouldContain(new DbObjectName(schemaName, DatabaseConstants.OutgoingTable));

        await conn.CloseAsync();
    }

    [Fact]
    public async Task find_all_the_agents()
    {
        var agents = await theFamily.AllKnownAgentsAsync();

        agents.ShouldContain(new Uri("wolverinedb://postgresql/localhost/postgres/wolverine"));
        agents.ShouldContain(new Uri("wolverinedb://postgresql/localhost/tenant3/wolverine"));
        agents.ShouldContain(new Uri("wolverinedb://postgresql/localhost/tenant2/wolverine"));
        agents.ShouldContain(new Uri("wolverinedb://postgresql/localhost/tenant1/wolverine"));
    }

    [Theory]
    [InlineData("wolverinedb://postgresql/localhost/postgres/wolverine")]
    [InlineData("wolverinedb://postgresql/localhost/tenant2/wolverine")]
    [InlineData("wolverinedb://postgresql/localhost/tenant1/wolverine")]
    [InlineData("wolverinedb://postgresql/localhost/tenant3/wolverine")]
    public async Task build_each_agent_smoke_test(string uriString)
    {
        var uri = uriString.ToUri();
        var agent = await theFamily.BuildAgentAsync(uri, theHost.GetRuntime());
        agent.ShouldNotBeNull();

        agent.Uri.ShouldBe(uri);
    }

    [Fact]
    public async Task try_to_use_the_session_transactional_middleware_end_to_end()
    {
        var message = new PlayerMessage(Guid.NewGuid().ToString());
        await theHost.InvokeMessageAndWaitAsync(message);

        var store = theHost.DocumentStore<IPlayerStore>();
        using var session = store.QuerySession();
        var player = await session.LoadAsync<Player>(message.Id);

        player.ShouldNotBeNull();
    }
}

public record PlayerMessage(string Id);

#region sample_PlayerMessageHandler

// This will use a Marten session from the
// IPlayerStore rather than the main IDocumentStore
[MartenStore(typeof(IPlayerStore))]
public static class PlayerMessageHandler
{
    // Using a Marten side effect just like normal
    public static IMartenOp Handle(PlayerMessage message)
    {
        return MartenOps.Store(new Player { Id = message.Id });
    }
}

#endregion

#region sample_separate_marten_stores

public interface IPlayerStore : IDocumentStore;

public interface IThingStore : IDocumentStore;

#endregion

public class Player
{
    public string Id { get; set; }
}

public class Thing
{
    public string Id { get; set; }
}