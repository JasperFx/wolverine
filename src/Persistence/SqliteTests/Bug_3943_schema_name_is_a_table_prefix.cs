using JasperFx.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Sqlite;
using Xunit;

namespace SqliteTests;

/// <summary>
/// Regression for https://github.com/JasperFx/wolverine/issues/3943.
///
/// <para>
/// A schema name — <c>PersistMessagesWithSqlite(connectionString, "custom")</c>, or
/// <c>FisherIntegration.MessageStorageSchemaName</c> — used to reach
/// <see cref="Wolverine.RDBMS.MessageDatabase{T}"/> as a <c>schema.table</c> qualifier, so the
/// durability SQL named <c>custom.wolverine_incoming_envelopes</c>. SQLite has no user-defined
/// schemas: the only ones a plain connection knows are <c>main</c>, <c>temp</c>, and whatever has
/// been ATTACHed, so the host built fine and then died on the first envelope write with
/// <c>SqliteException: no such table: custom.wolverine_incoming_envelopes</c>. Weasel's own
/// <see cref="SqliteObjectName"/> drops the schema from <c>QualifiedName</c>, so the DDL had been
/// creating a bare <c>wolverine_incoming_envelopes</c> the whole time — DDL and DML disagreed the
/// moment anyone set the property.
/// </para>
///
/// <para>
/// The fix folds the schema name into the table names as a prefix, which is what the property was
/// documented to do and what gives it a meaning SQLite can honour: separately nameable table sets
/// inside one database file. The default (<c>main</c>) prefixes nothing, so databases provisioned
/// before this change are untouched.
/// </para>
/// </summary>
public class Bug_3943_schema_name_is_a_table_prefix : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private IHost? _host;

    public ValueTask InitializeAsync()
    {
        _database = Servers.CreateDatabase(nameof(Bug_3943_schema_name_is_a_table_prefix));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _database.Dispose();
    }

    [Fact]
    public async Task a_custom_schema_name_prefixes_the_durability_tables()
    {
        _host = await startHostAsync("custom");

        var tables = await existingTableNamesAsync();

        tables.ShouldContain($"custom_{DatabaseConstants.IncomingTable}");
        tables.ShouldContain($"custom_{DatabaseConstants.OutgoingTable}");
        tables.ShouldContain($"custom_{DatabaseConstants.DeadLetterTable}");
        tables.ShouldContain($"custom_{DatabaseConstants.NodeTableName}");

        // The unprefixed names belong to a *different* logical table set, so a prefixed host must
        // not provision them. This is the half that makes two stores in one file worth having.
        tables.ShouldNotContain(DatabaseConstants.IncomingTable);
        tables.ShouldNotContain(DatabaseConstants.OutgoingTable);
    }

    [Fact]
    public async Task the_default_schema_name_leaves_the_table_names_alone()
    {
        // Guards the upgrade path: "main" is Wolverine.Sqlite's default, and every database
        // provisioned before GH-3943 has bare wolverine_* tables. Prefixing those would orphan them.
        _host = await startHostAsync(null);

        var tables = await existingTableNamesAsync();

        tables.ShouldContain(DatabaseConstants.IncomingTable);
        tables.ShouldContain(DatabaseConstants.OutgoingTable);
        tables.ShouldContain(DatabaseConstants.DeadLetterTable);
        tables.ShouldNotContain($"main_{DatabaseConstants.IncomingTable}");
    }

    [Fact]
    public async Task envelopes_round_trip_through_a_prefixed_store()
    {
        // The reported failure verbatim: the host starts, and then the first envelope write throws
        // "no such table: <name>.wolverine_incoming_envelopes". Everything here is inbox/outbox SQL
        // inherited from MessageDatabase<T>, which is exactly where the bad qualifier came from.
        _host = await startHostAsync("cw_chaingate_wolverine");

        var store = _host.Services.GetRequiredService<IMessageStore>();

        var incoming = ObjectMother.Envelope();
        incoming.Status = EnvelopeStatus.Incoming;
        await store.Inbox.StoreIncomingAsync(incoming);

        var outgoing = ObjectMother.Envelope();
        outgoing.Status = EnvelopeStatus.Outgoing;
        await store.Outbox.StoreOutgoingAsync(outgoing, 0);

        var counts = await store.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(1);
        counts.Outgoing.ShouldBe(1);

        await store.Inbox.MarkIncomingEnvelopeAsHandledAsync(incoming);

        var afterHandled = await store.Admin.FetchCountsAsync();
        afterHandled.Incoming.ShouldBe(0);
        afterHandled.Handled.ShouldBe(1);
    }

    [Fact]
    public async Task two_prefixed_table_sets_can_share_one_database_file()
    {
        // The point of the whole feature, and the shape the issue reports from: a monitoring console
        // whose store and whose Wolverine durability tables live in the same SQLite file.
        _host = await startHostAsync("alpha");

        using var second = await startHostAsync("beta");

        var tables = await existingTableNamesAsync();
        tables.ShouldContain($"alpha_{DatabaseConstants.IncomingTable}");
        tables.ShouldContain($"beta_{DatabaseConstants.IncomingTable}");

        var alpha = _host.Services.GetRequiredService<IMessageStore>();
        var beta = second.Services.GetRequiredService<IMessageStore>();

        await alpha.Inbox.StoreIncomingAsync(ObjectMother.Envelope());

        // Disjoint storage, not two views of one table.
        (await alpha.Admin.FetchCountsAsync()).Incoming.ShouldBe(1);
        (await beta.Admin.FetchCountsAsync()).Incoming.ShouldBe(0);

        await second.StopAsync(TestContext.Current.CancellationToken);
    }

    private Task<IHost> startHostAsync(string? schemaName)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_database.ConnectionString, schemaName);
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<List<string>> existingTableNamesAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var tables = await connection.ExistingTablesAsync(schemas: ["main"], ct: TestContext.Current.CancellationToken);
        return tables.Select(x => x.Name).ToList();
    }
}
