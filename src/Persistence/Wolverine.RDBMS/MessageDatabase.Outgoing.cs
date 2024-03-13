using System.Data.Common;
using Weasel.Core;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    public async Task StoreOutgoingAsync(DbTransaction tx, Envelope[] envelopes)
    {
        var cmd = DatabasePersistence.BuildOutgoingStorageCommand(envelopes, Durability.AssignedNodeNumber, this);
        cmd.Connection = tx.Connection;
        cmd.Transaction = tx;

        await cmd.ExecuteNonQueryAsync(_cancellation);

        foreach (var envelope in envelopes)
        {
            envelope.WasPersistedInOutbox = true;
        }
    }

    public abstract Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId);
    public abstract Task DeleteOutgoingAsync(Envelope[] envelopes);

    public async Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        return await _dataSource.CreateCommand(_outgoingEnvelopeSql)
            .With("destination", destination.ToString())
            .FetchListAsync(r => DatabasePersistence.ReadOutgoingAsync(r, _cancellation), _cancellation);
    }

    public Task DeleteOutgoingAsync(Envelope envelope)
    {
        if (HasDisposed) return Task.CompletedTask;
        
        return CreateCommand(
                $"delete from {SchemaName}.{DatabaseConstants.OutgoingTable} where id = @id")
            .With("id", envelope.Id)
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        if (HasDisposed) return;
        
        var command = DatabasePersistence.BuildOutgoingStorageCommand(envelope, ownerId, this);

        await using var conn = await DataSource.OpenConnectionAsync(_cancellation);

        try
        {
            command.Connection = conn;
            await command.ExecuteNonQueryAsync(_cancellation);
        }
        finally
        {
            await conn.CloseAsync();
        }

        envelope.WasPersistedInOutbox = true;
    }

    protected abstract string
        determineOutgoingEnvelopeSql(DurabilitySettings settings);
}