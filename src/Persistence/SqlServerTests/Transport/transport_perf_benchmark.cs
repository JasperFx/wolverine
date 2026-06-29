using System.Diagnostics;
using IntegrationTests;
using Microsoft.Data.SqlClient;
using Shouldly;
using Weasel.SqlServer;
using Xunit;
using Xunit.Abstractions;

namespace SqlServerTests.Transport;

// Manual benchmark for the SQL Server queue transport schema decision. Not part of CI.
// Compares three physical table designs head-to-head with raw DDL so the comparison is
// independent of Weasel migrations and Wolverine code:
//   A) baseline: clustered PRIMARY KEY on a random GUID, no timestamp index   (current)
//   B) clustered GUID PK + nonclustered index on timestamp                    (index-only)
//   C) nonclustered GUID PK + clustered bigint IDENTITY, dequeue by seq       (redesign)
// Run:
//   BENCH_SQLSERVER="Server=localhost,1443;..." dotnet test --filter "FullyQualifiedName~transport_perf_benchmark"
[Trait("Category", "Benchmark")]
public class transport_perf_benchmark
{
    private readonly ITestOutputHelper _output;
    public transport_perf_benchmark(ITestOutputHelper output) => _output = output;

    private static string ConnString =>
        Environment.GetEnvironmentVariable("BENCH_SQLSERVER") ?? Servers.SqlServerConnectionString;

    private const int BodySize = 250;
    private const int InsertCount = 3000;
    private const int DrainBatch = 50;
    private const int Backlog = 30000;
    private const int BacklogPops = 30;

    private record Variant(string Name, string Table, string CreateDdl, string OrderBy);

    private static Variant[] Variants() =>
    [
        new("A baseline (clustered GUID, no index)", "bench_a", $@"
CREATE TABLE bench_a (
    id uniqueidentifier NOT NULL CONSTRAINT pk_bench_a PRIMARY KEY,
    body varbinary(max) NOT NULL,
    message_type varchar(250) NOT NULL,
    keep_until datetimeoffset NULL,
    [timestamp] datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET());", "[timestamp]"),

        new("B clustered GUID + timestamp index", "bench_b", $@"
CREATE TABLE bench_b (
    id uniqueidentifier NOT NULL CONSTRAINT pk_bench_b PRIMARY KEY,
    body varbinary(max) NOT NULL,
    message_type varchar(250) NOT NULL,
    keep_until datetimeoffset NULL,
    [timestamp] datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET());
CREATE INDEX idx_bench_b_timestamp ON bench_b ([timestamp]);", "[timestamp]"),

        new("C nonclustered GUID PK + clustered identity seq", "bench_c", $@"
CREATE TABLE bench_c (
    id uniqueidentifier NOT NULL CONSTRAINT pk_bench_c PRIMARY KEY NONCLUSTERED,
    seq bigint IDENTITY(1,1) NOT NULL,
    body varbinary(max) NOT NULL,
    message_type varchar(250) NOT NULL,
    keep_until datetimeoffset NULL,
    [timestamp] datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET());
CREATE UNIQUE CLUSTERED INDEX cidx_bench_c_seq ON bench_c (seq);", "seq")
    ];

    // Validates that the real OptimizeQueueThroughput() table definition provisions through Weasel
    // (fresh create) and that a send -> ordered pop roundtrip works against the clustered seq layout.
    [Fact]
    public async Task verify_optimized_schema_provisions_and_roundtrips()
    {
        const string schema = "benchopt";
        await using (var conn = new SqlConnection(ConnString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(schema);
        }

        var transport = new Wolverine.SqlServer.Transport.SqlServerTransport(new Wolverine.RDBMS.DatabaseSettings
        {
            ConnectionString = ConnString,
            SchemaName = schema
        })
        {
            OptimizeQueueThroughput = true
        };

        var queue = transport.Queues["verify"];
        queue.Mode = Wolverine.Configuration.EndpointMode.BufferedInMemory;

        await queue.SetupAsync(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        // Clustered index must be on seq, not the id PK.
        await using (var conn = new SqlConnection(ConnString))
        {
            await conn.OpenAsync();
            var clusteredCol = (string?)await conn.CreateCommand(
                $@"select c.name from sys.indexes i
                   join sys.index_columns ic on i.object_id=ic.object_id and i.index_id=ic.index_id
                   join sys.columns c on ic.object_id=c.object_id and ic.column_id=c.column_id
                   where i.object_id = object_id('{schema}.wolverine_queue_verify') and i.type_desc='CLUSTERED'")
                .ExecuteScalarAsync();
            clusteredCol.ShouldBe("seq");
        }

        for (var i = 0; i < 25; i++)
        {
            var e = Wolverine.ComplianceTests.ObjectMother.Envelope();
            await queue.SendAsync(e, CancellationToken.None);
        }

        var popped = await queue.TryPopAsync(10, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        popped.Count.ShouldBe(10);
        (await queue.CountAsync()).ShouldBe(15);

        _output.WriteLine("Optimized schema provisioned; clustered on seq; roundtrip OK");
    }

    [Fact(Skip = "Manual benchmark; run explicitly with BENCH_SQLSERVER set")]
    public async Task run()
    {
        await using var conn = new SqlConnection(ConnString);
        await conn.OpenAsync();

        foreach (var v in Variants())
        {
            await execAsync(conn, $"IF OBJECT_ID('{v.Table}') IS NOT NULL DROP TABLE {v.Table};");
            await execAsync(conn, v.CreateDdl);

            // B1: insert throughput (single connection, mirrors per-message INSERT)
            var insertSql = $"insert into {v.Table} (id, body, message_type, keep_until) values (@id, @body, 'bench', NULL)";
            var body = new byte[BodySize];
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < InsertCount; i++)
            {
                await using var cmd = conn.CreateCommand(insertSql).With("id", Guid.NewGuid()).With("body", body);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync();
            }
            sw.Stop();
            var insertRate = InsertCount / sw.Elapsed.TotalSeconds;

            // B2: ordered drain throughput
            sw.Restart();
            long drained = 0;
            while (true)
            {
                var n = await popAsync(conn, v, DrainBatch);
                if (n == 0) break;
                drained += n;
            }
            sw.Stop();
            var drainRate = drained / sw.Elapsed.TotalSeconds;

            // B3: deep-backlog single-pop latency
            await bulkInsertAsync(conn, v, Backlog);
            var times = new List<double>();
            for (var i = 0; i < BacklogPops; i++)
            {
                var t = Stopwatch.StartNew();
                await popAsync(conn, v, DrainBatch);
                t.Stop();
                times.Add(t.Elapsed.TotalMilliseconds);
            }
            times.Sort();

            await execAsync(conn, $"DROP TABLE {v.Table};");

            _output.WriteLine(
                $"{v.Name}\n" +
                $"   B1 insert: {insertRate:F0} msg/s\n" +
                $"   B2 drain : {drainRate:F0} msg/s\n" +
                $"   B3 deep-pop (batch {DrainBatch} over {Backlog}): median {times[times.Count / 2]:F1} ms, " +
                $"p95 {times[(int)(times.Count * 0.95)]:F1} ms, max {times[^1]:F1} ms\n");
        }
    }

    private static async Task execAsync(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand(sql);
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> popAsync(SqlConnection conn, Variant v, int batch)
    {
        var sql = $@"
SET NOCOUNT ON;
WITH message AS (
    SELECT TOP(@count) body, keep_until
    FROM {v.Table} WITH (UPDLOCK, READPAST, ROWLOCK)
    ORDER BY {v.OrderBy})
DELETE FROM message OUTPUT deleted.body;";
        await using var cmd = conn.CreateCommand(sql).With("count", batch);
        cmd.CommandTimeout = 120;
        var rows = 0L;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows++;
        return rows;
    }

    private static async Task bulkInsertAsync(SqlConnection conn, Variant v, int count)
    {
        var sql = $@"
;WITH n AS (
    SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) r
    FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT INTO {v.Table} (id, body, message_type, keep_until)
SELECT NEWID(), CONVERT(varbinary(max), REPLICATE(CAST('x' AS varchar(max)), @size)), 'bench', NULL
FROM n;";
        await using var cmd = conn.CreateCommand(sql).With("count", count).With("size", BodySize);
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }
}
