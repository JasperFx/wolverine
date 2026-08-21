using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace SqlServerTests;

/// <summary>
/// GH-3971, the SQL Server half. The provider contributes its own <c>update top (n)</c> spelling of the
/// bounded release, so that statement needs to actually run somewhere.
///
/// <para>SQL Server deliberately does NOT override <c>DistinctOwnerIdsSql</c> and keeps the portable
/// <c>select distinct</c>: the recursive index skip-scan PostgreSQL uses cannot be written in T-SQL,
/// which forbids aggregates, TOP and subqueries in the recursive member of a recursive CTE. With the
/// <c>idx_wolverine_*_owner</c> index that DISTINCT is an index scan over a narrow key instead of a scan
/// over full envelope rows — a real improvement, just not the constant-time one Postgres gets. The
/// indexable, bounded UPDATE is the larger half of the fix and applies here in full.</para>
/// </summary>
public class orphaned_message_sweep_3971 : IAsyncLifetime
{
    private const string SchemaName = "orphan_sweep_3971";
    private IHost _host = null!;
    private IMessageDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Durability.DurabilityAgentEnabled = false;

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName);
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        _database = (IMessageDatabase)_host.GetRuntime().Storage;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<SqlConnection> openAsync()
    {
        var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task givenIncomingOwnedBy(params int[] owners)
    {
        await using var conn = await openAsync();

        foreach (var owner in owners)
        {
            await conn.CreateCommand(
                    $"insert into {SchemaName}.wolverine_incoming_envelopes (id, status, owner_id, body, message_type, received_at) values (@id, 'Incoming', @owner, @body, 'test', 'local://one')")
                .With("id", Guid.NewGuid())
                .With("owner", owner)
                .With("body", new byte[] { 1, 2, 3 })
                .ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private async Task<int[]> ownersInIncoming()
    {
        await using var conn = await openAsync();
        return (await conn.CreateCommand($"select owner_id from {SchemaName}.wolverine_incoming_envelopes")
                .FetchListAsync<int>(TestContext.Current.CancellationToken))
            .OrderBy(x => x).ToArray();
    }

    private Task sweepAsync(int[] activeNodeNumbers, int highWaterMark = 0)
    {
        var command = new ReleaseOrphanedMessagesCommand(_database, _host.GetRuntime().DurabilitySettings,
            NullLogger.Instance, activeNodeNumbers, highWaterMark);

        return command.ExecuteAsync(_host.GetRuntime(), CancellationToken.None);
    }

    [Fact]
    public async Task the_distinct_owner_sql_is_valid_and_finds_every_distinct_owner()
    {
        await givenIncomingOwnedBy(0, 3, 3, 7);

        var table = _database.DbObjectNameFor(DatabaseConstants.IncomingTable);

        await using var conn = await openAsync();
        var owners = await conn.CreateCommand(_database.DistinctOwnerIdsSql(table))
            .FetchListAsync<int>(TestContext.Current.CancellationToken);

        owners.OrderBy(x => x).ShouldBe([3, 7]);
    }

    [Fact]
    public async Task releases_only_the_rows_owned_by_departed_nodes()
    {
        await givenIncomingOwnedBy(1, 1, 2, 5, 9);

        await sweepAsync([1, 2]);

        (await ownersInIncoming()).ShouldBe([0, 0, 1, 1, 2]);
    }

    [Fact]
    public async Task leaves_everything_alone_when_no_owner_has_departed()
    {
        await givenIncomingOwnedBy(1, 2, 3);

        await sweepAsync([1, 2, 3]);

        (await ownersInIncoming()).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task the_bounded_update_drains_across_several_batches()
    {
        // The `update top (n)` statement is provider-specific and only exercised here. Prove the loop
        // drains rather than releasing one batch and stopping.
        _host.GetRuntime().DurabilitySettings.OrphanedMessageReleaseBatchSize = 10;

        await givenIncomingOwnedBy(Enumerable.Repeat(6, 35).ToArray());

        await sweepAsync([1]);

        var owners = await ownersInIncoming();
        owners.Length.ShouldBe(35);
        owners.ShouldAllBe(x => x == 0);
    }

    [Fact]
    public async Task the_per_cycle_batch_cap_is_honoured()
    {
        var settings = _host.GetRuntime().DurabilitySettings;
        settings.OrphanedMessageReleaseBatchSize = 10;
        settings.OrphanedMessageReleaseMaxBatchesPerCycle = 2;

        await givenIncomingOwnedBy(Enumerable.Repeat(6, 35).ToArray());

        await sweepAsync([1]);

        (await ownersInIncoming()).Count(x => x == 0).ShouldBe(20);
    }
}
