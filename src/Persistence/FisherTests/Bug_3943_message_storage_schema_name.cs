using Fisher;
using JasperFx;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.RDBMS;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
///     GH-3943: setting <see cref="FisherIntegration.MessageStorageSchemaName" /> used to kill the host
///     at the first envelope write with
///     <c>SqliteException: no such table: &lt;name&gt;.wolverine_incoming_envelopes</c>. The value was
///     documented as a naming prefix but reached <c>SqliteMessageStore</c> as a schema qualifier, and
///     SQLite has no user-defined schemas — <c>&lt;name&gt;</c> named a database nothing ever ATTACHed.
/// </summary>
/// <remarks>
///     This is the reporter's shape: a monitoring console standing up a third store flavour, carrying
///     over the same two configuration lines its Marten and Polecat flavours use. On SQLite the value
///     now prefixes the durability table names, which is the meaning the property was documented to
///     have and the one that lets Fisher's own <c>&lt;schema&gt;_fi_*</c> tables and Wolverine's
///     durability tables coexist in the one file under a shared naming convention.
/// </remarks>
public class Bug_3943_message_storage_schema_name : IAsyncLifetime
{
    private const string SchemaName = "cw_chaingate_wolverine";

    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("bug_3943");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RecordReadingHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine(w =>
                    {
                        w.MessageStorageSchemaName = SchemaName;
                        w.TransportSchemaName = SchemaName;
                    });
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task the_host_handles_a_message_through_the_outbox()
    {
        // Pre-fix this threw "no such table: cw_chaingate_wolverine.wolverine_incoming_envelopes".
        await theHost.InvokeMessageAndWaitAsync(new RecordReading("kasey", 42));

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var reading = await session.LoadAsync<Reading>("kasey", TestContext.Current.CancellationToken);
        reading.ShouldNotBeNull();
        reading.Value.ShouldBe(42);
    }

    [Fact]
    public async Task the_durability_tables_carry_the_name_as_a_prefix()
    {
        await using var connection = new SqliteConnection(theDatabase.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        (await tableExistsAsync(connection, $"{SchemaName}_{DatabaseConstants.IncomingTable}")).ShouldBeTrue();
        (await tableExistsAsync(connection, $"{SchemaName}_{DatabaseConstants.OutgoingTable}")).ShouldBeTrue();

        // And not as a qualifier against a database that was never ATTACHed.
        (await tableExistsAsync(connection, DatabaseConstants.IncomingTable)).ShouldBeFalse();
    }

    private static async Task<bool> tableExistsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = $name";
        command.Parameters.AddWithValue("$name", table);

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) > 0;
    }
}

public class Reading
{
    public string Id { get; set; } = null!;
    public int Value { get; set; }
}

public record RecordReading(string Name, int Value);

public static class RecordReadingHandler
{
    public static void Handle(RecordReading command, IDocumentSession session)
    {
        session.Store(new Reading { Id = command.Name, Value = command.Value });
    }
}
