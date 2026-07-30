using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using Xunit;

namespace PostgresqlTests.Agents;

/// <summary>
/// GH-3701: <c>wolverine_node_records</c> is append-only diagnostics with no foreign keys and no hot-path
/// reader, and until now nothing bounded its row count. <c>DeleteOldNodeRecordsAsync</c> was implemented for
/// every relational store but had no call site outside tests, and the only pruning that did run was the
/// age sweep against <c>NodeEventRecordExpirationTime</c> (5 days) — which is no ceiling at all on a cluster
/// writing one <c>AssignmentChanged</c> row per agent per assignment decision. The reporting cluster reached
/// 36,135,221 rows / 16 GB in five days, every one of those rows inside the age window.
///
/// This pins the call site: a running host must trim the table down to
/// <see cref="DurabilitySettings.NodeRecordRetention"/> on its own, with no test-side invocation of the
/// persistence method.
/// </summary>
[Collection("marten")]
public class node_record_retention : IAsyncLifetime
{
    private readonly string _schemaName = $"node_retention_{Guid.NewGuid().ToString("N")[..8]}";
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(_schemaName);
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, _schemaName);

                opts.Durability.NodeRecordRetention = 5;

                // The production cadence is hourly. Nothing here depends on the *period* being right, only
                // on the prune running at all, so shorten it to keep the test's poll window sane.
                opts.Durability.NodeRecordPruningPeriod = 1.Seconds();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task insertNodeRecordsAsync(int count)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        for (var i = 1; i <= count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"insert into {_schemaName}.{DatabaseConstants.NodeRecordTableName} (node_number, event_name, description) values ($1, $2, $3)";
            cmd.Parameters.AddWithValue(1);
            cmd.Parameters.AddWithValue(NodeRecordType.AssignmentChanged.ToString());
            cmd.Parameters.AddWithValue($"Record {i}");
            await cmd.ExecuteNonQueryAsync();
        }

        await conn.CloseAsync();
    }

    private async Task<int> countNodeRecordsAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {_schemaName}.{DatabaseConstants.NodeRecordTableName}";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        await conn.CloseAsync();
        return count;
    }

    [Fact]
    public async Task a_running_host_prunes_the_node_record_table_down_to_the_retention_cap()
    {
        await insertNodeRecordsAsync(50);
        (await countNodeRecordsAsync()).ShouldBeGreaterThanOrEqualTo(50);

        var count = 0;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            count = await countNodeRecordsAsync();
            if (count <= 5) break;
            await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);
        }

        // The host also writes its own NodeStarted/AgentStarted records while this runs, so the cap is the
        // ceiling, not an exact count. Before the fix this stayed at 50+ forever.
        count.ShouldBeLessThanOrEqualTo(5);
    }
}
