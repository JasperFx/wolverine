using Microsoft.Data.Sqlite;
using Wolverine.RDBMS;
using Wolverine.Sqlite;

namespace SqliteTests;

/// <summary>
/// GH-4565. <c>FisherIntegration</c> registers a <c>Discard()</c> rule for duplicate incoming messages. It
/// used to match on <c>SqliteException.SqliteExtendedErrorCode</c> alone -- 1555 or 2067 -- which is
/// <em>any</em> primary-key or unique-key violation anywhere in the handler's transaction. A handler that
/// committed a document with a duplicate natural key, or tripped a unique index on the application's own
/// table, had its message acknowledged and dropped: not retried, not dead-lettered, no dead letter to find
/// it in.
/// </summary>
/// <remarks>
/// Driven by exceptions SQLite actually raised rather than hand-built ones. The predicate reads the table
/// names out of the message text, which is the only place SQLite puts them, so a test over a fabricated
/// exception would be testing the fabrication.
/// </remarks>
public class duplicate_incoming_envelope_detection_4565 : IDisposable
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase("dup_detect_4565");

    public duplicate_incoming_envelope_detection_4565()
    {
        execute($"create table {DatabaseConstants.IncomingTable} (id text not null primary key)");

        // The prefixed form, for a host whose schema name is folded into the table name (GH-3943)
        execute($"create table tenant_a_{DatabaseConstants.IncomingTable} (id text not null primary key)");

        // An application table beside them, with both a primary key and a separate unique index
        execute("create table app_widgets (id text not null primary key, sku text not null unique)");
    }

    public void Dispose() => _database.Dispose();

    private void execute(string sql)
    {
        using var conn = new SqliteConnection(_database.ConnectionString);
        conn.Open();

        using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Run the same insert twice and hand back whatever SQLite threw the second time.
    /// </summary>
    private SqliteException duplicateViolation(string insert)
    {
        execute(insert);
        return Should.Throw<SqliteException>(() => execute(insert));
    }

    [Fact]
    public void a_duplicate_inbox_row_is_recognised()
    {
        var ex = duplicateViolation(
            $"insert into {DatabaseConstants.IncomingTable} (id) values ('{Guid.NewGuid()}')");

        ex.SqliteExtendedErrorCode.ShouldBe(1555);   // SQLITE_CONSTRAINT_PRIMARYKEY
        SqliteMessageStore.IsDuplicateIncomingEnvelope(ex).ShouldBeTrue();
    }

    [Fact]
    public void a_duplicate_inbox_row_is_recognised_through_a_table_prefix()
    {
        var ex = duplicateViolation(
            $"insert into tenant_a_{DatabaseConstants.IncomingTable} (id) values ('{Guid.NewGuid()}')");

        SqliteMessageStore.IsDuplicateIncomingEnvelope(ex).ShouldBeTrue();
    }

    /// <summary>
    /// The whole point of GH-4565: this is the one that used to be discarded.
    /// </summary>
    [Fact]
    public void a_duplicate_primary_key_on_an_application_table_is_NOT_recognised()
    {
        var id = Guid.NewGuid();

        // A DIFFERENT sku each time, so the violation is unambiguously the primary key. Reusing the sku
        // too makes SQLite report whichever constraint it happened to check first -- which is the unique
        // index, and then this test silently covers the same ground as its sibling below.
        execute($"insert into app_widgets (id, sku) values ('{id}', '{Guid.NewGuid():N}')");

        var ex = Should.Throw<SqliteException>(() =>
            execute($"insert into app_widgets (id, sku) values ('{id}', '{Guid.NewGuid():N}')"));

        // The same extended error code the old predicate matched on, so the old rule discarded this message
        ex.SqliteExtendedErrorCode.ShouldBe(1555);
        SqliteMessageStore.IsDuplicateIncomingEnvelope(ex).ShouldBeFalse();
    }

    [Fact]
    public void a_unique_index_violation_on_an_application_table_is_NOT_recognised()
    {
        var sku = Guid.NewGuid().ToString("N");

        execute($"insert into app_widgets (id, sku) values ('{Guid.NewGuid()}', '{sku}')");

        var ex = Should.Throw<SqliteException>(() =>
            execute($"insert into app_widgets (id, sku) values ('{Guid.NewGuid()}', '{sku}')"));

        ex.SqliteExtendedErrorCode.ShouldBe(2067);
        SqliteMessageStore.IsDuplicateIncomingEnvelope(ex).ShouldBeFalse();
    }

    [Fact]
    public void an_inbox_violation_is_found_through_a_wrapping_exception()
    {
        var ex = duplicateViolation(
            $"insert into {DatabaseConstants.IncomingTable} (id) values ('{Guid.NewGuid()}')");

        SqliteMessageStore.IsDuplicateIncomingEnvelope(new InvalidOperationException("outer", ex)).ShouldBeTrue();
        SqliteMessageStore.IsDuplicateIncomingEnvelope(new AggregateException(ex)).ShouldBeTrue();
    }

    [Fact]
    public void an_unrelated_exception_is_not_recognised()
    {
        SqliteMessageStore.IsDuplicateIncomingEnvelope(new InvalidOperationException("nope")).ShouldBeFalse();
    }
}
