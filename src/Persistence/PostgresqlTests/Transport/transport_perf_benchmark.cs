using System.Diagnostics;
using IntegrationTests;
using Npgsql;
using Weasel.Postgresql;
using Xunit;
using Xunit.Abstractions;

namespace PostgresqlTests.Transport;

// Manual benchmark for the PostgreSQL queue transport schema decision. Not part of CI.
// Postgres tables are heaps (no clustered index), so unlike SQL Server the win here is expected to
// come almost entirely from indexing the dequeue ordering column, not from any physical clustering.
// Compares three designs head-to-head with raw DDL:
//   A) baseline: PRIMARY KEY on a uuid id, no index on the ordering column   (current)
//   B) + btree index on timestamp                                           (index-only)
//   C) + bigint identity "seq" with a btree index, dequeue by seq           (monotonic ordering)
// Run:
//   BENCH_PG="Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres" \
//     dotnet test --filter "FullyQualifiedName~transport_perf_benchmark"
[Trait("Category", "Benchmark")]
public class transport_perf_benchmark
{
    private readonly ITestOutputHelper _output;
    public transport_perf_benchmark(ITestOutputHelper output) => _output = output;

    private static string ConnString =>
        Environment.GetEnvironmentVariable("BENCH_PG") ?? Servers.PostgresConnectionString;

    private const int BodySize = 250;
    private const int InsertCount = 3000;
    private const int DrainBatch = 50;
    private const int Backlog = 30000;
    private const int BacklogPops = 30;

    private record Variant(string Name, string Table, string CreateDdl, string OrderBy);

    private static Variant[] Variants() =>
    [
        new("A baseline (uuid PK, no index)", "bench_a", @"
CREATE TABLE bench_a (
    id uuid NOT NULL PRIMARY KEY,
    body bytea NOT NULL,
    message_type varchar NOT NULL,
    keep_until timestamptz NULL,
    ""timestamp"" timestamptz NOT NULL DEFAULT (now() at time zone 'utc'));", "\"timestamp\""),

        new("B uuid PK + timestamp index", "bench_b", @"
CREATE TABLE bench_b (
    id uuid NOT NULL PRIMARY KEY,
    body bytea NOT NULL,
    message_type varchar NOT NULL,
    keep_until timestamptz NULL,
    ""timestamp"" timestamptz NOT NULL DEFAULT (now() at time zone 'utc'));
CREATE INDEX idx_bench_b_timestamp ON bench_b (""timestamp"");", "\"timestamp\""),

        new("C uuid PK + bigint identity seq + seq index", "bench_c", @"
CREATE TABLE bench_c (
    id uuid NOT NULL PRIMARY KEY,
    seq bigint GENERATED ALWAYS AS IDENTITY,
    body bytea NOT NULL,
    message_type varchar NOT NULL,
    keep_until timestamptz NULL,
    ""timestamp"" timestamptz NOT NULL DEFAULT (now() at time zone 'utc'));
CREATE INDEX idx_bench_c_seq ON bench_c (seq);", "seq")
    ];

    [Fact(Skip = "Manual benchmark; run explicitly with BENCH_PG set")]
    public async Task run()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnString);
        await using var conn = await dataSource.OpenConnectionAsync();

        foreach (var v in Variants())
        {
            await exec(conn, $"DROP TABLE IF EXISTS {v.Table};");
            await exec(conn, v.CreateDdl);

            // B1: insert throughput (single connection, mirrors per-message INSERT)
            var insertSql = $"insert into {v.Table} (id, body, message_type) values (@id, @body, 'bench')";
            var body = new byte[BodySize];
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < InsertCount; i++)
            {
                await using var cmd = conn.CreateCommand(insertSql);
                cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("body", body);
                await cmd.ExecuteNonQueryAsync();
            }
            sw.Stop();
            var insertRate = InsertCount / sw.Elapsed.TotalSeconds;

            // B2: ordered drain throughput
            sw.Restart();
            long drained = 0;
            while (true)
            {
                var n = await pop(conn, v, DrainBatch);
                if (n == 0) break;
                drained += n;
            }
            sw.Stop();
            var drainRate = drained / sw.Elapsed.TotalSeconds;

            // B3: deep-backlog single-pop latency
            await bulkInsert(conn, v, Backlog);
            var times = new List<double>();
            for (var i = 0; i < BacklogPops; i++)
            {
                var t = Stopwatch.StartNew();
                await pop(conn, v, DrainBatch);
                t.Stop();
                times.Add(t.Elapsed.TotalMilliseconds);
            }
            times.Sort();

            await exec(conn, $"DROP TABLE {v.Table};");

            _output.WriteLine(
                $"{v.Name}\n" +
                $"   B1 insert: {insertRate:F0} msg/s\n" +
                $"   B2 drain : {drainRate:F0} msg/s\n" +
                $"   B3 deep-pop (batch {DrainBatch} over {Backlog}): median {times[times.Count / 2]:F1} ms, " +
                $"p95 {times[(int)(times.Count * 0.95)]:F1} ms, max {times[^1]:F1} ms\n");
        }
    }

    private static async Task exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand(sql);
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> pop(NpgsqlConnection conn, Variant v, int batch)
    {
        var sql = $@"
WITH message AS (
    DELETE FROM {v.Table}
    WHERE ctid IN (SELECT ctid FROM {v.Table} ORDER BY {v.OrderBy} LIMIT {batch} FOR UPDATE SKIP LOCKED)
    RETURNING body)
SELECT body FROM message;";
        await using var cmd = conn.CreateCommand(sql);
        cmd.CommandTimeout = 120;
        var rows = 0L;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows++;
        return rows;
    }

    private static async Task bulkInsert(NpgsqlConnection conn, Variant v, int count)
    {
        var sql = $@"
INSERT INTO {v.Table} (id, body, message_type)
SELECT gen_random_uuid(), decode(repeat('78', {BodySize}), 'hex'), 'bench'
FROM generate_series(1, {count});";
        await using var cmd = conn.CreateCommand(sql);
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }
}
