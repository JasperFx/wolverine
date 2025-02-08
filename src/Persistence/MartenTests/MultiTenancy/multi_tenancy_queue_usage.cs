
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using JasperFx.Resources;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace MartenTests.MultiTenancy;

public class multi_tenancy_queue_usage : PostgresqlContext, IAsyncLifetime
{
    private IHost _receiver;
    private IDocumentStore theStore;
    private string tenant1ConnectionString;
    private string tenant2ConnectionString;
    private string tenant3ConnectionString;
    private string tenant4ConnectionString;
    private IHost _sender;
    private MultiTenantedQueueListener? theListener;

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

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await conn.DropSchemaAsync("tenants");


        tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant1");
        tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant2");
        tenant3ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant3");
        tenant4ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant4");

        await conn.CloseAsync();

        // Start with a pair of tenants
        var tenancy = new MasterTableTenancy(new StoreOptions { DatabaseSchemaName = "tenants" },
            Servers.PostgresConnectionString, "tenants");
        await tenancy.ClearAllDatabaseRecordsAsync();
        await tenancy.AddDatabaseRecordAsync("tenant1", tenant1ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant2", tenant2ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant3", tenant3ConnectionString);

        // Setting up a Host with Multi-tenancy
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This is too extreme for real usage, but helps tests to run faster
                opts.Durability.NodeReassignmentPollingTime = 1.Seconds();
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.TenantCheckPeriod = 250.Milliseconds();

                opts.Durability.Mode = DurabilityMode.Solo;

                opts.ListenToPostgresqlQueue("one");

                opts.Services.AddMarten(o =>
                    {
                        // This is a new strategy for configuring tenant databases with Marten
                        // In this usage, Marten is tracking the tenant databases in a single table in the "master"
                        // database by tenant
                        o.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString, "tenants");
                    })
                    .IntegrateWithWolverine(m =>
                    {
                        m.MessageStorageSchemaName = "mt";
                        m.MasterDatabaseConnectionString = Servers.PostgresConnectionString;
                    })

                    // All detected changes will be applied to all
                    // the configured tenant databases on startup
                    .ApplyAllDatabaseChangesOnStartup();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This is too extreme for real usage, but helps tests to run faster
                opts.Durability.NodeReassignmentPollingTime = 1.Seconds();
                opts.Durability.HealthCheckPollingTime = 1.Seconds();

                opts.Durability.Mode = DurabilityMode.Solo;

                opts.PublishMessage<CreateTenantDoc>().ToPostgresqlQueue("one");

                opts.Services.AddMarten(o =>
                    {
                        // This is a new strategy for configuring tenant databases with Marten
                        // In this usage, Marten is tracking the tenant databases in a single table in the "master"
                        // database by tenant
                        o.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString, "tenants");
                    })
                    .IntegrateWithWolverine(m =>
                    {
                        m.MessageStorageSchemaName = "mt";
                        m.MasterDatabaseConnectionString = Servers.PostgresConnectionString;
                    })

                    // All detected changes will be applied to all
                    // the configured tenant databases on startup
                    .ApplyAllDatabaseChangesOnStartup();

                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync();

        theStore = _receiver.Services.GetRequiredService<IDocumentStore>();
        await theStore.Advanced.Clean.DeleteAllDocumentsAsync();

        theListener = (MultiTenantedQueueListener)_receiver
            .GetRuntime()
            .Endpoints
            .ActiveListeners()
            .Single(x => x.Uri == new Uri("postgresql://one")).As<ListeningAgent>()
            .Listener;

    }

    public async Task DisposeAsync()
    {
        await _receiver.StopAsync();
        _receiver.Dispose();

        await _sender.StopAsync();
        _sender.Dispose();
    }

    [Fact]
    public void has_active_listeners_for_the_existing_databases()
    {
        theListener.IsListeningToDatabase("tenant1").ShouldBeTrue();
        theListener.IsListeningToDatabase("tenant2").ShouldBeTrue();
    }

    [Fact]
    public async Task spin_up_new_databases_and_see_listeners_be_created()
    {
        var tenancy = (MasterTableTenancy)theStore.Options.Tenancy;
        await tenancy.AddDatabaseRecordAsync("tenant3", tenant3ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant4", tenant4ConnectionString);

        for (int i = 0; i < 10; i++)
        {
            var has3 = theListener.IsListeningToDatabase("tenant3");
            var has4 = theListener.IsListeningToDatabase("tenant4");

            if (has3 && has4) return;

            await Task.Delay(250.Milliseconds());
        }

        throw new TimeoutException("Did not detect the two new per tenant listeners were started up");
    }

    [Fact]
    public async Task send_message_through_tenant()
    {
        var message = new CreateTenantDoc("Blue", 10);
        var tracked = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(message, new DeliveryOptions { TenantId = "tenant3" });

        tracked.Received.SingleEnvelope<CreateTenantDoc>()
            .Destination.ShouldBe(new Uri("postgresql://one/tenant3"));

        await using var session = theStore.LightweightSession("tenant3");
        var doc = await session.LoadAsync<TenantDoc>(message.Id);
        doc.ShouldNotBeNull();
        doc.Number.ShouldBe(10);
    }
}