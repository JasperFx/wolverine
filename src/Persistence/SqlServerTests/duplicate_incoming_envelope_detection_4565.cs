using IntegrationTests;
using Microsoft.Data.SqlClient;
using Shouldly;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Persistence;

namespace SqlServerTests;

/// <summary>
/// GH-4565. <c>PolecatIntegration</c> registers a <c>Discard()</c> rule for duplicate incoming messages.
/// It used to match on <c>SqlException.Number</c> alone -- 2627 or 2601 -- which is <em>any</em> primary-key
/// or unique-key violation anywhere in the handler's transaction. A handler that committed a document with
/// a duplicate natural key, or tripped a unique index on the application's own table, had its message
/// acknowledged and dropped: not retried, not dead-lettered, no dead letter to find it in.
/// </summary>
/// <remarks>
/// Driven by exceptions SQL Server actually raised rather than hand-built ones. The predicate reads the
/// object name out of the message text, which is the only place SQL Server puts it, so a test over a
/// fabricated exception would be testing the fabrication.
/// </remarks>
public class duplicate_incoming_envelope_detection_4565 : IAsyncLifetime
{
    private readonly string _schema = "dup_detect_4565";

    public async ValueTask InitializeAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await executeAsync(conn, $"if schema_id('{_schema}') is null exec('create schema {_schema}')");

        // The inbox table, named exactly as Wolverine names it, and an application table beside it. Only
        // the identity column matters here -- the point is which object the violation reports.
        await executeAsync(conn, $"drop table if exists {_schema}.{DatabaseConstants.IncomingTable}");
        await executeAsync(conn,
            $"create table {_schema}.{DatabaseConstants.IncomingTable} (id uniqueidentifier not null, " +
            $"constraint pkey_{DatabaseConstants.IncomingTable}_id primary key (id))");

        await executeAsync(conn, $"drop table if exists {_schema}.app_widgets");
        await executeAsync(conn,
            $"create table {_schema}.app_widgets (id uniqueidentifier not null, sku varchar(50) not null, " +
            $"constraint pkey_app_widgets_id primary key (id), constraint uq_app_widgets_sku unique (sku))");

        // And the prefixed form, for a host whose schema name is folded into the table name
        await executeAsync(conn, $"drop table if exists {_schema}.tenant_a_{DatabaseConstants.IncomingTable}");
        await executeAsync(conn,
            $"create table {_schema}.tenant_a_{DatabaseConstants.IncomingTable} (id uniqueidentifier not null, " +
            $"constraint pkey_tenant_a_incoming primary key (id))");
    }

    public async ValueTask DisposeAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        foreach (var table in new[]
                 {
                     DatabaseConstants.IncomingTable, "app_widgets", $"tenant_a_{DatabaseConstants.IncomingTable}"
                 })
        {
            await executeAsync(conn, $"drop table if exists {_schema}.{table}");
        }
    }

    private static async Task executeAsync(SqlConnection conn, string sql)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Insert the same row twice and hand back whatever SQL Server threw the second time.
    /// </summary>
    private static async Task<SqlException> duplicateViolationAsync(string insert)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await executeAsync(conn, insert);

        return await Should.ThrowAsync<SqlException>(() => executeAsync(conn, insert));
    }

    [Fact]
    public async Task a_duplicate_inbox_row_is_recognised()
    {
        var id = Guid.NewGuid();
        var ex = await duplicateViolationAsync(
            $"insert into {_schema}.{DatabaseConstants.IncomingTable} (id) values ('{id}')");

        ex.Number.ShouldBe(2627);
        SqlServerMessageStore.IsDuplicateIncomingEnvelope(ex).ShouldBeTrue();
    }

    [Fact]
    public async Task a_duplicate_inbox_row_is_recognised_through_a_table_prefix()
    {
        var id = Guid.NewGuid();
        var ex = await duplicateViolationAsync(
            $"insert into {_schema}.tenant_a_{DatabaseConstants.IncomingTable} (id) values ('{id}')");

        SqlServerMessageStore.IsDuplicateIncomingEnvelope(ex).ShouldBeTrue();
    }

    /// <summary>
    /// The whole point of GH-4565: this is the one that used to be discarded.
    /// </summary>
    [Fact]
    public async Task a_duplicate_primary_key_on_an_application_table_is_NOT_recognised()
    {
        var id = Guid.NewGuid();
        var ex = await duplicateViolationAsync(
            $"insert into {_schema}.app_widgets (id, sku) values ('{id}', '{Guid.NewGuid():N}')");

        // Same error number the old predicate matched on, so the old rule discarded this message
        ex.Number.ShouldBe(2627);
        SqlServerMessageStore.IsDuplicateIncomingEnvelope(ex).ShouldBeFalse();
    }

    [Fact]
    public async Task a_unique_index_violation_on_an_application_table_is_NOT_recognised()
    {
        var sku = Guid.NewGuid().ToString("N");

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await executeAsync(conn, $"insert into {_schema}.app_widgets (id, sku) values ('{Guid.NewGuid()}', '{sku}')");

        var ex = await Should.ThrowAsync<SqlException>(() => executeAsync(conn,
            $"insert into {_schema}.app_widgets (id, sku) values ('{Guid.NewGuid()}', '{sku}')"));

        ex.Number.ShouldBe(2627);
        SqlServerMessageStore.IsDuplicateIncomingEnvelope(ex).ShouldBeFalse();
    }

    [Fact]
    public async Task an_inbox_violation_is_found_through_a_wrapping_exception()
    {
        var id = Guid.NewGuid();
        var ex = await duplicateViolationAsync(
            $"insert into {_schema}.{DatabaseConstants.IncomingTable} (id) values ('{id}')");

        // The store's own commit path wraps, and so do storage providers
        SqlServerMessageStore.IsDuplicateIncomingEnvelope(new InvalidOperationException("outer", ex))
            .ShouldBeTrue();
        SqlServerMessageStore.IsDuplicateIncomingEnvelope(new AggregateException(ex)).ShouldBeTrue();
    }

    [Fact]
    public void an_unrelated_exception_is_not_recognised()
    {
        SqlServerMessageStore.IsDuplicateIncomingEnvelope(new InvalidOperationException("nope")).ShouldBeFalse();
    }
}
