using JasperFx.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using Xunit;

namespace SqliteTests;

/// <summary>
/// Regression for https://github.com/JasperFx/wolverine/issues/2680.
///
/// When <see cref="DurabilitySettings.MessageIdentity"/> is set to
/// <see cref="MessageIdentity.IdAndDestination"/>, both the <c>id</c> and
/// <c>received_at</c> columns of <c>wolverine_incoming_envelopes</c> are declared
/// as <c>PRIMARY KEY</c> inline by <see cref="Wolverine.Sqlite.Schema.IncomingEnvelopeTable"/>
/// (each calls <c>.AsPrimaryKey()</c>). SQLite rejects two inline primary-key columns
/// with <c>'table … has more than one primary key'</c> and the table is never created.
/// The durability agent then crashes on every poll querying the missing table.
///
/// The fix needs SQLite DDL to use a single table-level composite constraint instead:
/// <code>
/// CREATE TABLE IF NOT EXISTS wolverine_incoming_envelopes (
///     id          TEXT       NOT NULL,
///     ...
///     received_at TEXT       NOT NULL,
///     keep_until  TEXT,
///     PRIMARY KEY (id, received_at)
/// );
/// </code>
///
/// This test exercises the exact host startup from the bug report and asserts the
/// <c>wolverine_incoming_envelopes</c> table actually exists with both <c>id</c> and
/// <c>received_at</c> participating in the primary key.
/// </summary>
public class Bug_2680_message_identity_id_and_destination_emits_invalid_ddl : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private IHost? _host;

    public Task InitializeAsync()
    {
        _database = Servers.CreateDatabase(nameof(Bug_2680_message_identity_id_and_destination_emits_invalid_ddl));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _database.Dispose();
    }

    [Fact]
    public async Task host_starts_and_creates_inbox_table_with_composite_primary_key()
    {
        // Mirrors the bug report's repro verbatim — opting into IdAndDestination identity
        // before pointing Wolverine at a fresh sqlite file and asking for resource setup
        // on startup. Pre-fix this throws SqliteException("table 'wolverine_incoming_envelopes'
        // has more than one primary key") during StartAsync; post-fix the migration succeeds
        // and the table exists with the expected composite key.
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
                opts.PersistMessagesWithSqlite(_database.ConnectionString);
                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync();

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var tables = await connection.ExistingTablesAsync(schemas: ["main"]);
        tables.ShouldContain(
            x => string.Equals(x.Name, DatabaseConstants.IncomingTable, StringComparison.OrdinalIgnoreCase),
            $"{DatabaseConstants.IncomingTable} must exist after host startup; pre-fix the migration aborts and the table is never created.");

        // Confirm the migration actually produced a composite primary key, not a single-column
        // accidental win where one of the two AsPrimaryKey calls happened to be the only one
        // SQLite kept (or some other non-bug-shape DDL). PRAGMA table_info exposes the pk slot
        // for each column: 0 = not in PK, 1+ = position within the composite key.
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({DatabaseConstants.IncomingTable});";

        var pkColumns = new List<string>();
        await using (var reader = await pragma.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var columnName = reader.GetString(1);
                var pk = reader.GetInt32(5);
                if (pk > 0)
                {
                    pkColumns.Add(columnName);
                }
            }
        }

        pkColumns.ShouldContain("id");
        pkColumns.ShouldContain("received_at");
        pkColumns.Count.ShouldBe(2,
            $"expected exactly id + received_at in the composite primary key, got: {string.Join(", ", pkColumns)}");
    }
}
