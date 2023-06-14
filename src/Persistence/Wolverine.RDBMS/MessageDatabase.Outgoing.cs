using System.Data.Common;
using Weasel.Core;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    public Task StoreOutgoingAsync(DbTransaction tx, Envelope[] envelopes)
    {
        var cmd = DatabasePersistence.BuildOutgoingStorageCommand(envelopes, Durability.AssignedNodeNumber, this);
        cmd.Connection = tx.Connection;
        cmd.Transaction = tx;

        return cmd.ExecuteNonQueryAsync(_cancellation);
    }

    public abstract Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId);
    public abstract Task DeleteOutgoingAsync(Envelope[] envelopes);

    public async Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        var envelopes = await conn.CreateCommand(_outgoingEnvelopeSql)
            .With("destination", destination.ToString())
            .FetchListAsync(r => DatabasePersistence.ReadOutgoingAsync(r, _cancellation), _cancellation);

        await conn.CloseAsync();

        return envelopes;
    }

    public Task DeleteOutgoingAsync(Envelope envelope)
    {
        return CreateCommand(
                $"delete from {SchemaName}.{DatabaseConstants.OutgoingTable} where id = @id")
            .With("id", envelope.Id)
            .ExecuteOnce(_cancellation);
    }

    public Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        return DatabasePersistence.BuildOutgoingStorageCommand(envelope, ownerId, this)
            .ExecuteOnce(_cancellation);
    }

    protected abstract string
        determineOutgoingEnvelopeSql(DurabilitySettings settings);
}