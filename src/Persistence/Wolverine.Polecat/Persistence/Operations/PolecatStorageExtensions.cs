using Microsoft.Data.SqlClient;
using Polecat;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;
using Wolverine.SqlServer.Persistence;

namespace Wolverine.Polecat.Persistence.Operations;

internal static class PolecatStorageExtensions
{
    public static void StoreIncoming(this IDocumentSession session, SqlServerMessageStore store, Envelope envelope)
    {
        var participant = new StoreIncomingEnvelopeParticipant(store.IncomingFullName, envelope);
        session.AddTransactionParticipant(participant);
    }

    public static void StoreOutgoing(this IDocumentSession session, SqlServerMessageStore store, Envelope envelope,
        int ownerId)
    {
        var participant = new StoreOutgoingEnvelopeParticipant(store.OutgoingFullName, envelope, ownerId);
        session.AddTransactionParticipant(participant);
    }
}

internal class StoreIncomingEnvelopeParticipant : ITransactionParticipant
{
    private readonly string _incomingTable;
    private readonly Envelope _envelope;

    public StoreIncomingEnvelopeParticipant(string incomingTable, Envelope envelope)
    {
        _incomingTable = incomingTable;
        _envelope = envelope;
    }

    public async Task BeforeCommitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            $"insert into {_incomingTable} ({DatabaseConstants.IncomingFields}) values (@body, @id, @status, @ownerId, @scheduledTime, @attempts, @messageType, @destination, @keepUntil)";

        cmd.Parameters.AddWithValue("@body", EnvelopeSerializer.Serialize(_envelope));
        cmd.Parameters.AddWithValue("@id", _envelope.Id);
        cmd.Parameters.AddWithValue("@status", _envelope.Status.ToString());
        cmd.Parameters.AddWithValue("@ownerId", _envelope.OwnerId);
        cmd.Parameters.AddWithValue("@scheduledTime",
            _envelope.ScheduledTime.HasValue ? _envelope.ScheduledTime.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@attempts", _envelope.Attempts);
        cmd.Parameters.AddWithValue("@messageType", _envelope.MessageType);
        cmd.Parameters.AddWithValue("@destination",
            (object?)_envelope.Destination?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@keepUntil",
            _envelope.KeepUntil.HasValue ? _envelope.KeepUntil.Value : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(token);
    }
}

internal class StoreOutgoingEnvelopeParticipant : ITransactionParticipant
{
    private readonly string _outgoingTable;
    private readonly Envelope _envelope;
    private readonly int _ownerId;

    public StoreOutgoingEnvelopeParticipant(string outgoingTable, Envelope envelope, int ownerId)
    {
        _outgoingTable = outgoingTable;
        _envelope = envelope;
        _ownerId = ownerId;
    }

    public async Task BeforeCommitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            $"insert into {_outgoingTable} ({DatabaseConstants.OutgoingFields}) values (@body, @id, @ownerId, @destination, @deliverBy, @attempts, @messageType)";

        cmd.Parameters.AddWithValue("@body", EnvelopeSerializer.Serialize(_envelope));
        cmd.Parameters.AddWithValue("@id", _envelope.Id);
        cmd.Parameters.AddWithValue("@ownerId", _ownerId);
        cmd.Parameters.AddWithValue("@destination", _envelope.Destination!.ToString());
        cmd.Parameters.AddWithValue("@deliverBy",
            _envelope.DeliverBy.HasValue ? _envelope.DeliverBy.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@attempts", _envelope.Attempts);
        cmd.Parameters.AddWithValue("@messageType", _envelope.MessageType);

        await cmd.ExecuteNonQueryAsync(token);
    }
}
