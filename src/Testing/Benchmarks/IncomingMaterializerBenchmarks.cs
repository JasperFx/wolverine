using System.Data.Common;
using BenchmarkDotNet.Attributes;
using IntegrationTests;
using Npgsql;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;

namespace Benchmarks;

/// <summary>
/// GH-5146, "ReadIncomingAsync materializer (measure)". <see cref="DatabasePersistence.ReadIncomingAsync" />
/// reads all nine columns of an inbox row with <c>GetFieldValueAsync</c> and <c>IsDBNullAsync</c>, which is
/// one Task per column per row. The readers Wolverine uses are opened without
/// <c>CommandBehavior.SequentialAccess</c>, so by the time <c>ReadAsync</c> returns the row is already
/// buffered and a synchronous getter cannot block. This measures whether a synchronous materializer is
/// worth having, over a real Npgsql reader against the docker-compose Postgres (port 5433), so the
/// numbers include the driver's own per-column cost and not just ours.
///
/// <para>Run: <c>dotnet run -c Release -f net10.0 --project src/Testing/Benchmarks -- --filter '*IncomingMaterializer*'</c></para>
/// </summary>
[MemoryDiagnoser]
public class IncomingMaterializerBenchmarks
{
    private const string Schema = "wv_bench_5146";

    // Matches the column order of DatabaseConstants.IncomingFields, which is what ReadIncomingAsync expects
    private const string Select =
        $"select body, id, status, owner_id, execution_time, attempts, message_type, received_at, keep_until from {Schema}.incoming";

    private NpgsqlDataSource _dataSource = null!;

    [Params(1, 100, 1000)] public int Rows;

    [GlobalSetup]
    public async Task Setup()
    {
        _dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);

        await using var conn = await _dataSource.OpenConnectionAsync();
        await execute(conn, $"drop schema if exists {Schema} cascade");
        await execute(conn, $"create schema {Schema}");
        await execute(conn, $"""
            create table {Schema}.incoming (
                body bytea not null, id uuid primary key, status varchar not null, owner_id int not null,
                execution_time timestamptz null, attempts int not null, message_type varchar not null,
                received_at varchar not null, keep_until timestamptz null)
            """);

        var payload = new byte[1024];
        Random.Shared.NextBytes(payload);

        for (var i = 0; i < Rows; i++)
        {
            var envelope = new Envelope
            {
                Id = Guid.NewGuid(),
                MessageType = "Benchmarks.SomeMessage",
                Destination = new Uri("rabbitmq://queue/incoming"),
                CorrelationId = Guid.NewGuid().ToString(),
                ContentType = "application/json",
                Data = payload
            };

            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"insert into {Schema}.incoming values (@body, @id, 'Incoming', 0, null, 0, @type, @uri, null)";
            cmd.Parameters.AddWithValue("body", EnvelopeSerializer.Serialize(envelope));
            cmd.Parameters.AddWithValue("id", envelope.Id);
            cmd.Parameters.AddWithValue("type", envelope.MessageType!);
            cmd.Parameters.AddWithValue("uri", envelope.Destination!.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await using (var conn = await _dataSource.OpenConnectionAsync())
        {
            await execute(conn, $"drop schema if exists {Schema} cascade");
        }

        await _dataSource.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> AsyncPerColumn()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Select;
        await using var reader = await cmd.ExecuteReaderAsync();

        var list = new List<Envelope>(Rows);
        while (await reader.ReadAsync()) list.Add(await DatabasePersistence.ReadIncomingAsync(reader));
        return list.Count;
    }

    [Benchmark]
    public async Task<int> SyncAfterReadAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Select;
        await using var reader = await cmd.ExecuteReaderAsync();

        var list = new List<Envelope>(Rows);
        while (await reader.ReadAsync()) list.Add(readIncoming(reader));
        return list.Count;
    }

    // The candidate: the same reads as ReadIncomingAsync, with the synchronous getters and no Enum.Parse
    private static Envelope readIncoming(DbDataReader reader)
    {
        var body = reader.GetFieldValue<byte[]>(0);
        var envelope = body.Length > 0 ? EnvelopeSerializer.Deserialize(body) : new Envelope();
        envelope.Id = reader.GetFieldValue<Guid>(1);
        envelope.Status = reader.GetString(2) switch
        {
            "Incoming" => EnvelopeStatus.Incoming,
            "Scheduled" => EnvelopeStatus.Scheduled,
            "Handled" => EnvelopeStatus.Handled,
            var other => Enum.Parse<EnvelopeStatus>(other)
        };
        envelope.OwnerId = reader.GetFieldValue<int>(3);
        envelope.MessageType = reader.GetString(6);
        envelope.Destination = new Uri(reader.GetString(7));

        if (!reader.IsDBNull(4)) envelope.ScheduledTime = reader.GetFieldValue<DateTimeOffset>(4);
        envelope.Attempts = reader.GetFieldValue<int>(5);
        if (!reader.IsDBNull(8)) envelope.KeepUntil = reader.GetFieldValue<DateTimeOffset>(8);

        return envelope;
    }

    private static async Task execute(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// GH-5146, "EnvelopeSerializer on ReadOnlyMemoryStream". gRPC and Pub/Sub hand Wolverine the wire bytes as
/// a protobuf <c>ByteString</c>; the listeners used to call <c>ToByteArray()</c> (a full copy) and then
/// <see cref="EnvelopeSerializer.Deserialize(byte[])" />. They now pass <c>ByteString.Memory</c> to the
/// <see cref="ReadOnlyMemory{T}" /> overload. This compares the two on the same bytes.
/// </summary>
[MemoryDiagnoser]
public class EnvelopeReadFromMemoryBenchmarks
{
    private ReadOnlyMemory<byte> _wire;

    [Params(1024, 100 * 1024)] public int PayloadSize;

    [GlobalSetup]
    public void Setup()
    {
        var payload = new byte[PayloadSize];
        Random.Shared.NextBytes(payload);
        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "Benchmarks.SomeMessage",
            Destination = new Uri("grpc://localhost:5000"),
            CorrelationId = Guid.NewGuid().ToString(),
            ContentType = "application/json",
            Data = payload
        };

        _wire = EnvelopeSerializer.Serialize(envelope);
    }

    [Benchmark(Baseline = true)]
    public Envelope CopyThenDeserialize() => EnvelopeSerializer.Deserialize(_wire.ToArray());

    [Benchmark]
    public Envelope DeserializeMemory() => EnvelopeSerializer.Deserialize(_wire);
}
