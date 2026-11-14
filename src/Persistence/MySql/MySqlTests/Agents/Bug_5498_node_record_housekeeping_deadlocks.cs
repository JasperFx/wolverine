using System.Collections.Concurrent;
using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime.Agents;
using Xunit;

namespace MySqlTests.Agents;

/// <summary>
/// GH-5498: on MySQL the two clean-ups of <c>wolverine_node_records</c> deadlocked. The row trim's floor-id
/// subquery was a locking read inside its DELETE and deadlocked with node-record inserts, and the age sweep had
/// no timestamp index, so it next-key locked the whole table in the opposite order to the trim and the two
/// deadlocked whenever they overlapped.
/// </summary>
[Collection("mysql")]
public class Bug_5498_node_record_housekeeping_deadlocks : IAsyncLifetime
{
    private const string SchemaName = "node_record_deadlocks";
    private MySqlMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{SchemaName}`";
            await cmd.ExecuteNonQueryAsync();
        }

        var dataSource = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString);
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.MySqlConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        _store = new MySqlMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<MySqlMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await _store.Admin.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    private ISweepExpiredNodeRecords theSweep => _store.Nodes.ShouldBeAssignableTo<ISweepExpiredNodeRecords>()!;

    private async Task insertNodeRecordsAsync(MySqlConnection conn, int count, string prefix = "Record")
    {
        // Several inserts in one transaction, as PersistNodeRecord does inside a DatabaseOperationBatch
        await using var tx = await conn.BeginTransactionAsync();
        for (var i = 1; i <= count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"INSERT INTO {SchemaName}.{DatabaseConstants.NodeRecordTableName} (node_number, event_name, description) VALUES (@number, @event, @description)";
            cmd.Parameters.AddWithValue("number", 1);
            cmd.Parameters.AddWithValue("event", NodeRecordType.AssignmentChanged.ToString());
            cmd.Parameters.AddWithValue("description", $"{prefix} {i:00}");
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private async Task executeAsync(string sql)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task the_node_record_table_has_a_timestamp_index_and_reports_no_drift()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = @schema AND table_name = @table AND column_name = 'timestamp'";
        cmd.Parameters.AddWithValue("schema", SchemaName);
        cmd.Parameters.AddWithValue("table", DatabaseConstants.NodeRecordTableName);

        Convert.ToInt32(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBeGreaterThan(0);

        // The issue found that an index added outside Wolverine is dropped by the next migration, so the
        // index has to be part of the table definition -- and must not show up as drift on every pass
        await _store.Admin.MigrateAsync();
        await _store.AssertDatabaseMatchesConfigurationAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_age_sweep_deletes_only_expired_records()
    {
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await insertNodeRecordsAsync(conn, 4, "Old");
        }

        await executeAsync(
            $"UPDATE {SchemaName}.{DatabaseConstants.NodeRecordTableName} SET timestamp = UTC_TIMESTAMP(6) - INTERVAL 10 DAY");

        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await insertNodeRecordsAsync(conn, 3, "New");
        }

        await theSweep.DeleteExpiredNodeRecordsAsync(DateTimeOffset.UtcNow.AddDays(-5), TestContext.Current.CancellationToken);

        var remaining = await _store.Nodes.FetchRecentRecordsAsync(100);
        remaining.Select(x => x.Description).OrderBy(x => x).ShouldBe(["New 01", "New 02", "New 03"]);
    }

    [Fact]
    public async Task the_age_sweep_on_an_empty_table_does_not_throw()
    {
        await theSweep.DeleteExpiredNodeRecordsAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        (await _store.Nodes.FetchRecentRecordsAsync(100)).Count.ShouldBe(0);
    }

    [Fact]
    public async Task the_age_sweep_keeps_a_newer_record_with_a_lower_id()
    {
        // The id bound comes from MAX(id) of the expired rows, but the delete keeps the timestamp predicate
        // too, so a row inside the bound that is not yet expired survives
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await insertNodeRecordsAsync(conn, 3);
        }

        await executeAsync(
            $"UPDATE {SchemaName}.{DatabaseConstants.NodeRecordTableName} SET timestamp = UTC_TIMESTAMP(6) - INTERVAL 10 DAY WHERE description <> 'Record 02'");

        await theSweep.DeleteExpiredNodeRecordsAsync(DateTimeOffset.UtcNow.AddDays(-5), TestContext.Current.CancellationToken);

        var remaining = await _store.Nodes.FetchRecentRecordsAsync(100);
        remaining.Select(x => x.Description).ShouldBe(["Record 02"]);
    }

    [Fact]
    public async Task trim_and_sweep_do_not_deadlock_with_each_other_or_with_inserts()
    {
        var failures = new ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = stop.Token;

        // Four "nodes" writing node records, as the issue's reproduction does
        var inserters = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await insertNodeRecordsAsync(conn, 3);
                }
                catch (Exception e)
                {
                    failures.Enqueue(e);
                }
            }
        })).ToArray();

        var trims = 0;
        var sweeps = 0;

        // Back to back instead of on the pruning timer, and the trim and the sweep running at the same time,
        // which is the overlap the production deadlock came from
        var trimmer = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _store.Nodes.DeleteOldNodeRecordsAsync(50);
                    Interlocked.Increment(ref trims);
                }
                catch (Exception e)
                {
                    failures.Enqueue(e);
                }
            }
        }, TestContext.Current.CancellationToken);

        var sweeper = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await theSweep.DeleteExpiredNodeRecordsAsync(DateTimeOffset.UtcNow.AddMilliseconds(-200),
                        TestContext.Current.CancellationToken);
                    Interlocked.Increment(ref sweeps);
                }
                catch (Exception e)
                {
                    failures.Enqueue(e);
                }
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll([..inserters, trimmer, sweeper]);

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures.Take(5).Select(x => x.Message)));
        trims.ShouldBeGreaterThan(0);
        sweeps.ShouldBeGreaterThan(0);
    }
}
