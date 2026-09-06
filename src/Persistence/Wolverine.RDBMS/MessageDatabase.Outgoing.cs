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
                $"delete from {QuotedTableNameFor(DatabaseConstants.OutgoingTable)} where id = @id")
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

    /// <summary>
    /// GH-4319. The batched twin of the single-envelope overload above: one pooled connection and one
    /// multi-statement command for the whole batch instead of one of each per envelope. The batch
    /// command builder was already here -- <c>DurableSendingAgent</c> simply had no way to reach it
    /// outside an ambient transaction.
    /// </summary>
    public async Task StoreOutgoingAsync(IReadOnlyList<Envelope> envelopes, int ownerId)
    {
        if (HasDisposed || envelopes.Count == 0) return;

        var array = envelopes as Envelope[] ?? envelopes.ToArray();
        var command = DatabasePersistence.BuildOutgoingStorageCommand(array, ownerId, this);

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

        foreach (var envelope in array)
        {
            envelope.WasPersistedInOutbox = true;
        }
    }

    protected abstract string
        determineOutgoingEnvelopeSql(DurabilitySettings settings);
}