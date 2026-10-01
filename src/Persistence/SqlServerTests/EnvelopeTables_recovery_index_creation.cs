using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Schema;
using Wolverine.Tracking;

namespace SqlServerTests;

// GH-4316: the 5-second recovery poll and the per-listener recovery load ask for
// `owner_id = 0` — exactly the value the GH-3971 owner index excludes — and the expired-handled
// cleanup filters on `status = 'Handled' and keep_until <= now` with no index at all, so all of
// them were full scans of an inbox dominated by retained Handled rows. The envelope tables now
// provision filtered indexes for the recoverable and expired-handled slices. These tests prove the
// indexes are created AND that their compound filter predicates round-trip through sys.indexes
// (SqlServer stores e.g. `([status]='Incoming' AND [owner_id]=(0))`, which must canonicalize back
// to the configured predicate or Weasel drops+recreates the index on every startup).
[Collection("sqlserver")]
public class EnvelopeTables_recovery_index_creation : IAsyncLifetime
{
    private SqlConnection theConnection = null!;

    public async ValueTask InitializeAsync()
    {
        theConnection = new SqlConnection(Servers.SqlServerConnectionString);
        await theConnection.OpenAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theConnection.DisposeAsync();
    }

    [Fact]
    public async Task incoming_recovery_indexes_are_created_and_stable()
    {
        await theConnection.ResetSchemaAsync("env_idx_incoming", ct: TestContext.Current.CancellationToken);

        var table = new IncomingEnvelopeTable(new DurabilitySettings(), "env_idx_incoming");

        table.Indexes.ShouldContain(x => x.Name.Contains("recover"));
        table.Indexes.ShouldContain(x => x.Name.Contains("keep_until"));
        table.Indexes.ShouldContain(x => x.Name.Contains("scheduled"));

        await table.ApplyChangesAsync(theConnection, ct: TestContext.Current.CancellationToken);

        // Re-reading the just-created schema must report NO difference. If a filtered-index
        // predicate did not round-trip, this would come back as Update and thrash on every startup.
        var delta = await table.FindDeltaAsync(theConnection, TestContext.Current.CancellationToken);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task outgoing_recovery_index_is_created_and_stable()
    {
        await theConnection.ResetSchemaAsync("env_idx_outgoing", ct: TestContext.Current.CancellationToken);

        var table = new OutgoingEnvelopeTable(new DurabilitySettings(), "env_idx_outgoing");

        table.Indexes.ShouldContain(x => x.Name.Contains("recover"));

        await table.ApplyChangesAsync(theConnection, ct: TestContext.Current.CancellationToken);

        var delta = await table.FindDeltaAsync(theConnection, TestContext.Current.CancellationToken);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }
}

/// <summary>
/// GH-4739. Provisioning the owner index is only half the job — the release statement has to be written so
/// SQL Server can actually use it. <c>idx_wolverine_incoming_envelopes_owner</c> is FILTERED on
/// <c>owner_id &lt;&gt; 0</c>, and SQL Server will not use a filtered index for a parameterized
/// <c>owner_id = @owner</c>: the cached plan has to stay correct for <c>@owner = 0</c>, which the filter
/// excludes. So <c>ReleaseIncomingAsync</c> was a clustered index scan of the whole inbox on every listener
/// drain, regardless of the data — the reporter measured 6,748 logical reads against 3 with 20k retained
/// <c>Handled</c> rows, and in production (1.6M rows / 6.8 GB) it timed out five listeners during a
/// blue/green deploy. The redundant-looking <c>and owner_id &lt;&gt; 0</c> makes the filter provable at
/// compile time and the scan becomes a seek.
/// </summary>
public abstract class ReleaseIncomingContext : IAsyncLifetime
{
    protected const int TheOwner = 7;
    protected const int AnotherOwner = 9;

    protected static readonly Uri TheAddress = new("local://one");

    private IHost _host = null!;

    protected IMessageDatabase theDatabase = null!;

    protected abstract string SchemaName { get; }

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

        theDatabase = (IMessageDatabase)_host.GetRuntime().Storage;

        await seedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    protected abstract Task seedAsync();

    protected async Task<SqlConnection> openAsync()
    {
        var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// The in-flight work every case starts from: two rows owned by the node that is about to release, one
    /// owned by a different node, and one already unowned. Only the first two may ever move.
    /// </summary>
    protected Task givenTheInFlightRowsAsync() =>
        givenIncomingOwnedByAsync(TheOwner, TheOwner, AnotherOwner, 0);

    protected async Task givenIncomingOwnedByAsync(params int[] owners)
    {
        await using var conn = await openAsync();

        foreach (var owner in owners)
        {
            await conn.CreateCommand(
                    $"insert into {SchemaName}.{DatabaseConstants.IncomingTable} (id, status, owner_id, body, message_type, received_at) values (@id, 'Incoming', @owner, @body, 'test', @uri)")
                .With("id", Guid.NewGuid())
                .With("owner", owner)
                .With("body", new byte[] { 1, 2, 3 })
                .With("uri", TheAddress.ToString())
                .ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    protected async Task<int[]> inFlightOwnersAsync()
    {
        await using var conn = await openAsync();
        return (await conn.CreateCommand(
                    $"select owner_id from {SchemaName}.{DatabaseConstants.IncomingTable} where status = 'Incoming'")
                .FetchListAsync<int>(CancellationToken.None))
            .OrderBy(x => x).ToArray();
    }
}

/// <summary>
/// GH-4739, the behaviour half: the extra <c>owner_id &lt;&gt; 0</c> clause is redundant by construction —
/// owner 0 IS the unowned marker, so no row can both be owned by a node and be unowned — but "redundant"
/// is exactly the sort of claim that deserves a test. Releasing has to still release this node's rows for
/// this listener, and still leave everybody else's alone.
/// </summary>
[Collection("sqlserver")]
public class release_incoming_ownership_4739 : ReleaseIncomingContext
{
    protected override string SchemaName => "release_incoming_4739";

    protected override Task seedAsync() => givenTheInFlightRowsAsync();

    [Fact]
    public async Task releases_only_this_nodes_rows_for_this_listener()
    {
        await theDatabase.Inbox.ReleaseIncomingAsync(TheOwner, TheAddress);

        // The two rows owned by TheOwner join the one that was already unowned; AnotherOwner keeps its row.
        (await inFlightOwnersAsync()).ShouldBe([0, 0, 0, AnotherOwner]);
    }

    [Fact]
    public async Task leaves_another_nodes_rows_alone()
    {
        await theDatabase.Inbox.ReleaseIncomingAsync(AnotherOwner, TheAddress);

        (await inFlightOwnersAsync()).ShouldBe([0, 0, TheOwner, TheOwner]);
    }

    [Fact]
    public async Task releases_nothing_for_a_listener_that_owns_no_rows()
    {
        await theDatabase.Inbox.ReleaseIncomingAsync(TheOwner, new Uri("local://nothing-here"));

        (await inFlightOwnersAsync()).ShouldBe([0, TheOwner, TheOwner, AnotherOwner]);
    }

    /// <summary>
    /// The InboxHealthRestarter probe passes owner 0 deliberately. Nothing may move, and — the part that
    /// matters — the call must still reach the database and still throw when the store is unreachable, so
    /// it must never be short-circuited in managed code. Negative control for the "still throws" half:
    /// src/Testing/CoreTests/Transports/inbox_health_probe_4658.cs.
    /// </summary>
    [Fact]
    public async Task the_health_probes_owner_zero_release_moves_nothing()
    {
        await theDatabase.Inbox.ReleaseIncomingAsync(0, new Uri("wolverine://inbox-health-probe"));

        (await inFlightOwnersAsync()).ShouldBe([0, TheOwner, TheOwner, AnotherOwner]);
    }
}

/// <summary>
/// GH-4739, the regression that matters. Measures the REAL statement the production code issues, through its
/// plan-cache entry in <c>sys.dm_exec_query_stats</c>, rather than a copy retyped in the test — a retyped
/// copy would keep passing if the production SQL regressed.
/// </summary>
[Collection("sqlserver")]
public class release_incoming_uses_the_owner_index_4739 : ReleaseIncomingContext
{
    /// <summary>
    /// Retained <c>Handled</c> rows sitting at <c>owner_id = 0</c> — the shape of a real busy inbox, and the
    /// rows the filtered owner index deliberately excludes. Padded so the table spans thousands of pages: at
    /// 20k narrow rows a scan and a seek are only ~10x apart, which is not a margin worth asserting on.
    /// </summary>
    private const int RetainedHandledRows = 20_000;

    private const int BodyPadding = 2_000;

    protected override string SchemaName => "release_incoming_idx_4739";

    protected override async Task seedAsync()
    {
        await using var conn = await openAsync();

        // One set-based insert rather than 20k round trips. sys.all_objects cross joined with itself is the
        // standard T-SQL numbers generator and is plentiful enough for this row count.
        await conn.CreateCommand(
                $"""
                 with n as (select top (@rows) row_number() over (order by (select null)) as i
                            from sys.all_objects a cross join sys.all_objects b)
                 insert into {SchemaName}.{DatabaseConstants.IncomingTable}
                     (id, status, owner_id, body, message_type, received_at, keep_until)
                 select newid(), 'Handled', 0,
                        convert(varbinary(max), replicate(cast('x' as varchar(max)), @padding)),
                        'test', @uri, dateadd(hour, 1, sysdatetimeoffset())
                 from n
                 """)
            .With("rows", RetainedHandledRows)
            .With("padding", BodyPadding)
            .With("uri", TheAddress.ToString())
            .ExecuteNonQueryAsync(CancellationToken.None);

        await givenTheInFlightRowsAsync();

        // Auto-stats would fire on first use anyway, but what the optimizer believes about row counts is the
        // whole subject here, so make it current and deterministic rather than timing-dependent.
        await conn.CreateCommand(
                $"update statistics {SchemaName}.{DatabaseConstants.IncomingTable} with fullscan")
            .ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>
    /// How SQL Server reached the rows, per index on the inbox table: <c>user_seeks</c> and <c>user_scans</c>
    /// from <c>sys.dm_db_index_usage_stats</c>, keyed by index name.
    ///
    /// <para>This is the instrument rather than <c>sys.dm_exec_query_stats</c> on purpose.
    /// <c>dm_exec_query_stats</c> carries the logical-read counts the issue was reported in, but finding the
    /// statement in it means a <c>cross apply sys.dm_exec_sql_text</c> over the whole plan cache — on a shared
    /// CI container, with every other suite's plans in there too, that query itself times out. The usage
    /// counters are per-object, cheap, and answer the actual question: seek or scan.</para>
    /// </summary>
    private async Task<Dictionary<string, (long Seeks, long Scans)>> indexAccessAsync()
    {
        await using var conn = await openAsync();

        var access = new Dictionary<string, (long Seeks, long Scans)>();

        await using var reader = await conn.CreateCommand(
                """
                select i.name, isnull(s.user_seeks, 0), isnull(s.user_scans, 0)
                from sys.indexes i
                left join sys.dm_db_index_usage_stats s
                    on s.object_id = i.object_id and s.index_id = i.index_id and s.database_id = db_id()
                where i.object_id = object_id(@table)
                """)
            .With("table", $"{SchemaName}.{DatabaseConstants.IncomingTable}")
            .ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {
            access[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        }

        return access;
    }

    private async Task<int> inboxPageCountAsync()
    {
        await using var conn = await openAsync();

        var raw = await conn.CreateCommand(
                """
                select isnull(sum(a.total_pages), 0)
                from sys.partitions p
                join sys.allocation_units a on a.container_id = p.hobt_id
                where p.object_id = object_id(@table)
                """)
            .With("table", $"{SchemaName}.{DatabaseConstants.IncomingTable}")
            .ExecuteScalarAsync(CancellationToken.None);

        return Convert.ToInt32(raw);
    }

    /// <summary>
    /// Without <c>and owner_id &lt;&gt; 0</c> the release is a clustered index scan: the owner index records no
    /// seek and the clustered index records a scan. With it, exactly the other way round. Cross-checked against
    /// <c>sys.dm_exec_query_stats</c> for this exact fixture on the repo's SQL Server image — 17,449 logical
    /// reads for the statement without the clause against 33 with it — which is the same effect the reporter
    /// measured at 6,748 against 3.
    /// </summary>
    [Fact]
    public async Task the_release_seeks_the_owner_index_instead_of_scanning_the_inbox()
    {
        var inboxPages = await inboxPageCountAsync();
        inboxPages.ShouldBeGreaterThan(2_000,
            "the fixture has to be big enough that the plan choice is worth thousands of reads");

        var clusteredIndex = await clusteredIndexNameAsync();
        var ownerIndex = $"idx_{DatabaseConstants.IncomingTable}_owner";

        var before = await indexAccessAsync();
        before.Keys.ShouldContain(ownerIndex, "the GH-3971 owner index has to exist for any of this to mean anything");

        // Nothing but the release may touch the inbox between these two snapshots -- the durability agent is
        // off, and inFlightOwnersAsync() (itself a clustered scan) is deliberately left until afterwards.
        await theDatabase.Inbox.ReleaseIncomingAsync(TheOwner, TheAddress);

        var after = await indexAccessAsync();

        (after[ownerIndex].Seeks - before[ownerIndex].Seeks).ShouldBe(1L,
            $"the release did not seek {ownerIndex}; it cannot while the statement's `owner_id = @owner` is unprovable against the index's `owner_id <> 0` filter");

        (after[clusteredIndex].Scans - before[clusteredIndex].Scans).ShouldBe(0L,
            $"the release scanned the whole inbox ({inboxPages} pages) instead of seeking {ownerIndex}");

        // And it still did the job.
        (await inFlightOwnersAsync()).ShouldBe([0, 0, 0, AnotherOwner]);
    }

    private async Task<string> clusteredIndexNameAsync()
    {
        await using var conn = await openAsync();

        var raw = await conn.CreateCommand(
                "select name from sys.indexes where object_id = object_id(@table) and index_id = 1")
            .With("table", $"{SchemaName}.{DatabaseConstants.IncomingTable}")
            .ExecuteScalarAsync(CancellationToken.None);

        return (string)raw!;
    }
}
