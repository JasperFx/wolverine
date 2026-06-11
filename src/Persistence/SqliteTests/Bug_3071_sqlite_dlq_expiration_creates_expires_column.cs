using JasperFx.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Sqlite;
using Xunit;

namespace SqliteTests;

/// <summary>
/// Regression for https://github.com/JasperFx/wolverine/issues/3071.
///
/// <para>
/// With <see cref="DurabilitySettings.DeadLetterQueueExpirationEnabled"/> set, the
/// Sqlite <see cref="Wolverine.Sqlite.Schema.DeadLettersTable"/> provisioned a
/// column named <c>keep_until</c>. The shared DLQ insert path in
/// <see cref="Wolverine.RDBMS.DatabasePersistence.WriteDeadLetter"/> and the
/// expiration cleanup query in
/// <see cref="Wolverine.RDBMS.Durability.DeleteExpiredDeadLetterMessagesOperation"/>
/// both target <see cref="DatabaseConstants.Expires"/> (= <c>expires</c>) — the
/// name every other RDBMS backend uses (Postgres, SqlServer, MySql, Oracle).
/// Net effect on a Sqlite-backed host with DLQ expiration enabled:
/// <c>SqliteException: no such column: expires</c> on the cleanup job and on
/// any DLQ insert (the report calls out both: fresh DB AND existing DB).
/// </para>
///
/// <para>
/// The fix names the column <c>expires</c> so the shared SQL works as-is. This
/// regression mirrors the report verbatim: spin up a host with
/// <c>PersistMessagesWithSqlite(...)</c> + DLQ expiration enabled, ask for
/// resource setup on startup, then assert the dead-letter table exists with an
/// <c>expires</c> column (and that the legacy <c>keep_until</c> column is NOT
/// what gets provisioned).
/// </para>
/// </summary>
public class Bug_3071_sqlite_dlq_expiration_creates_expires_column : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private IHost? _host;

    public Task InitializeAsync()
    {
        _database = Servers.CreateDatabase(nameof(Bug_3071_sqlite_dlq_expiration_creates_expires_column));
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
    public async Task dlq_table_has_expires_column_when_expiration_is_enabled()
    {
        // The reporter's exact startup shape: PersistMessagesWithSqlite + DLQ
        // expiration enabled + resource setup. Pre-fix the table provisions a
        // `keep_until` column and the shared SQL aimed at `expires` throws
        // SqliteException("no such column: expires") on the next cleanup pass.
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_database.ConnectionString);
                opts.Durability.DeadLetterQueueExpirationEnabled = true;
                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync();

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var tables = await connection.ExistingTablesAsync(schemas: ["main"]);
        tables.ShouldContain(
            x => string.Equals(x.Name, DatabaseConstants.DeadLetterTable, StringComparison.OrdinalIgnoreCase),
            $"{DatabaseConstants.DeadLetterTable} must exist after host startup with DeadLetterQueueExpirationEnabled.");

        var columns = await GetColumnNamesAsync(connection, DatabaseConstants.DeadLetterTable);
        columns.ShouldContain(
            DatabaseConstants.Expires,
            $"{DatabaseConstants.DeadLetterTable} must carry the '{DatabaseConstants.Expires}' column — that's what " +
            $"the shared DatabasePersistence.WriteDeadLetter insert path and " +
            $"DeleteExpiredDeadLetterMessagesOperation cleanup query both reference.");
        columns.ShouldNotContain(
            DatabaseConstants.KeepUntil,
            $"{DatabaseConstants.DeadLetterTable} must NOT carry the legacy '{DatabaseConstants.KeepUntil}' column " +
            $"that pre-fix Sqlite provisioned by accident — the shared SQL never wrote to it.");
    }

    [Fact]
    public async Task dlq_table_has_no_expires_column_when_expiration_is_disabled()
    {
        // Regression guard the other way: with expiration off, neither
        // `expires` nor `keep_until` should appear on the DLQ table. Without
        // this, a careless future refactor could re-introduce the bug under
        // a different code path.
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_database.ConnectionString);
                opts.Durability.DeadLetterQueueExpirationEnabled = false;
                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync();

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var columns = await GetColumnNamesAsync(connection, DatabaseConstants.DeadLetterTable);
        columns.ShouldNotContain(DatabaseConstants.Expires);
        columns.ShouldNotContain(DatabaseConstants.KeepUntil);
    }

    private static async Task<List<string>> GetColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        // PRAGMA table_info(tbl) is Sqlite's column-list reflection surface;
        // column index 1 is the column name. Used here rather than a Weasel
        // FetchExistingAsync round-trip so the assertion shape is independent
        // of any future Weasel-side normalization choice on column names.
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName});";

        var names = new List<string>();
        await using var reader = await pragma.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }
        return names;
    }
}
