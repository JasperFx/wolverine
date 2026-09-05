using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Weasel.Core;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.ModularMonoliths;

/// <summary>
/// The wolverine_recurring_messages tracking table is MAIN-DATABASE-ONLY bookkeeping — in a
/// modular monolith whose module owns an ancillary store on its own database, the ancillary
/// database gets its own envelope tables but must NEVER grow the recurring table: the single
/// cluster-wide recurring agent publishes through the main store, and a second copy of the table
/// would be tracking rows nothing reads. The "absent on the ancillary database" assertion is
/// paired with an "envelope tables present there" guard, so an ancillary store that simply failed
/// to migrate cannot pass this test vacuously.
/// </summary>
public class recurring_table_is_main_database_only_in_a_modular_monolith : IAsyncLifetime
{
    // Dedicated durability schema so the physical assertions and the pre-clean touch nothing any
    // other suite provisions in the shared "wolverine" schema.
    private const string SchemaName = "recurring_mono";

    private IHost _host = null!;
    private string _ancillaryConnectionString = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();

            var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);
            var exists = await conn.DatabaseExists("database1");
            if (!exists)
            {
                await new DatabaseSpecification().BuildDatabase(conn, "database1");
            }

            builder.Database = "database1";
            _ancillaryConnectionString = builder.ConnectionString;
        }

        // Idempotence: stale tables left by an older build must not decide this run.
        foreach (var connectionString in new[] { Servers.PostgresConnectionString, _ancillaryConnectionString })
        {
            await dropSchema(connectionString);
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType(typeof(MonolithRecurringMessageHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageStorageSchemaName = SchemaName;

                // The main store...
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                // ...and a module's ancillary store on its OWN database, the modular monolith shape.
                opts.Services.AddMartenStore<IFirstStore>(m =>
                {
                    m.Connection(_ancillaryConnectionString);
                    m.DatabaseSchemaName = "first_recurring";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                // The registration is the feature's opt-in — set before any migration runs.
                opts.Schedules.ScheduleRecurring<MonolithRecurringMessage>(
                    "monolith-shape", "0 * * * *", _ => new MonolithRecurringMessage());
            })
            .ConfigureServices(services => services.AddResourceSetupOnStartup())
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_table_is_on_main_and_never_on_the_ancillary_database()
    {
        // Main database: migrated, and carrying the recurring table.
        var mainTables = await tablesInSchema(Servers.PostgresConnectionString);
        mainTables.ShouldContain(DatabaseConstants.IncomingTable);
        mainTables.ShouldContain(DatabaseConstants.RecurringMessagesTableName);

        // Ancillary database: its own envelope tables exist (the guard that keeps this honest) —
        // and the recurring table does not.
        var ancillaryTables = await tablesInSchema(_ancillaryConnectionString);
        ancillaryTables.ShouldContain(DatabaseConstants.IncomingTable,
            "the ancillary database was never migrated, so the absence assertion below would be vacuous");
        ancillaryTables.ShouldNotContain(DatabaseConstants.RecurringMessagesTableName);

        // The runtime agrees: only the main store carries a live recurring tracking store.
        var runtime = _host.GetRuntime();
        runtime.Storage.RecurringMessages.Enabled.ShouldBeTrue();

        var ancillary = runtime.Stores.FindAncillaryStore(typeof(IFirstStore));
        ancillary.ShouldNotBeNull();
        ancillary.Role.ShouldBe(MessageStoreRole.Ancillary);
    }

    private static async Task dropSchema(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await conn.RunSqlAsync($"drop schema if exists {SchemaName} cascade");
    }

    private static async Task<string[]> tablesInSchema(string connectionString)
    {
        var list = new List<string>();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select table_name from information_schema.tables where table_schema = @schema";
        cmd.Parameters.AddWithValue("schema", SchemaName);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list.ToArray();
    }
}

public class MonolithRecurringMessage;

public static class MonolithRecurringMessageHandler
{
    public static void Handle(MonolithRecurringMessage message)
    {
    }
}
