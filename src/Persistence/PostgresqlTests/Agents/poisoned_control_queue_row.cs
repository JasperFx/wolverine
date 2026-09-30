using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Tracking;

namespace PostgresqlTests.Agents;

/// <summary>
/// GH-4718. A control queue row this node cannot deserialize used to abort the ENTIRE poll:
/// PollDatabaseControlQueue.ReadResultsAsync threw out of the DatabaseOperationBatch, so the shared
/// transaction never committed -- taking DeleteExpiredMessages down with it, which is what would otherwise
/// have cleared the offending row 30 seconds later. The node then re-read the same row every poll and
/// received NO control message at all for as long as it sat there.
///
/// That is the reported stall: agent assignment frozen for 30-60 minutes after every node change, with the
/// leader logging "confirmed 0 of N requested agents" for chunk after perfectly-sized chunk, because the
/// destination's control queue was dead. The only trace was an InvalidEnvelopeException naming nothing but
/// "IAgentCommand", and the reporter could not find the envelope in any table.
/// </summary>
public class poisoned_control_queue_row : PostgresqlContext, IAsyncLifetime
{
    private const string SchemaName = "pgpoison";

    private IHost _sender = null!;
    private IHost _receiver = null!;
    private Uri _receiverUri = null!;
    private Guid _receiverNodeId;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(SchemaName);
            await conn.CloseAsync();
        }

        _sender = await startHost("PoisonSender");
        _receiver = await startHost("PoisonReceiver");

        _receiverNodeId = _receiver.GetRuntime().Options.UniqueNodeId;
        _receiverUri = new Uri($"dbcontrol://{_receiverNodeId}");
    }

    private static Task<IHost> startHost(string serviceName)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.ServiceName = serviceName;
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    /// <summary>
    /// Written straight into the table so the row is exactly what the field produced: addressed to the
    /// receiver, and -- crucially -- with an expiry an hour out, so the test proves the POLL survives it
    /// rather than quietly measuring DeleteExpiredMessages sweeping it up from the other node 30s later.
    /// </summary>
    private async Task insertPoisonRow(string messageType, byte[] body)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand(
            $"insert into {SchemaName}.wolverine_control_queue (id, message_type, node_id, body, expires) values (:id, :messagetype, :node, :body, :expires)");
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, Guid.NewGuid());
        cmd.Parameters.AddWithValue("messagetype", NpgsqlDbType.Varchar, messageType);
        cmd.Parameters.AddWithValue("node", NpgsqlDbType.Uuid, _receiverNodeId);
        cmd.Parameters.AddWithValue("body", NpgsqlDbType.Bytea, body);
        cmd.Parameters.AddWithValue("expires", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow.AddHours(1));
        await cmd.ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }

    private async Task<long> countControlRowsForReceiver()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand(
            $"select count(*) from {SchemaName}.wolverine_control_queue where node_id = :node");
        cmd.Parameters.AddWithValue("node", NpgsqlDbType.Uuid, _receiverNodeId);

        var count = (long)(await cmd.ExecuteScalarAsync())!;

        await conn.CloseAsync();
        return count;
    }

    /// <summary>
    /// An otherwise well-formed envelope whose payload is past Options.MaxIncomingEnvelopeDataSize. This is
    /// the GH-4718 body exactly: Wolverine enforces MaxDataSize when READING and never when writing, so it
    /// was producing its own control traffic in a size it then refused to read back.
    /// </summary>
    private static byte[] oversizedEnvelopeBody()
    {
        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "Wolverine.Runtime.Agents.IAgentCommand",
            ContentType = EnvelopeConstants.JsonContentType,
            Data = new byte[EnvelopeSerializer.Limits.MaxDataSize + 1024]
        };

        return EnvelopeSerializer.Serialize(envelope);
    }

    private async Task assertReceiverStillGetsControlMessages()
    {
        var tracked = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(m => m.EndpointFor(_receiverUri).SendAsync(new Command(42)));

        tracked.Received.RecordsInOrder()
            .Single(x => x.Envelope!.Message!.GetType() == typeof(Command))
            .ServiceName!.ShouldBe("PoisonReceiver");
    }

    [Fact]
    public async Task an_oversized_row_does_not_block_the_rest_of_the_control_queue()
    {
        await insertPoisonRow("Wolverine.Runtime.Agents.IAgentCommand", oversizedEnvelopeBody());

        // THE GH-4718 defect: without the per-row quarantine this never arrives, because every poll dies on
        // the row above before it reads anything else addressed to this node.
        await assertReceiverStillGetsControlMessages();
    }

    [Fact]
    public async Task an_unreadable_row_is_discarded_so_it_cannot_poison_later_polls()
    {
        await insertPoisonRow("Wolverine.Runtime.Agents.IAgentCommand", oversizedEnvelopeBody());

        await assertReceiverStillGetsControlMessages();

        // The row has no other way out of the table -- it never becomes an Envelope, so nothing in the normal
        // delete path can reach it, and its own expiry was rolled back with the failed batch.
        (await countControlRowsForReceiver()).ShouldBe(0);
    }

    [Fact]
    public async Task structurally_corrupt_bytes_are_survived_the_same_way()
    {
        await insertPoisonRow("Something.Unparseable", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        await assertReceiverStillGetsControlMessages();

        (await countControlRowsForReceiver()).ShouldBe(0);
    }
}
