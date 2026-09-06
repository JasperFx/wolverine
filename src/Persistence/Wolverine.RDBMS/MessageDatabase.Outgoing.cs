using System.Data.Common;
using Weasel.Core;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    /// <summary>
    /// GH-4375. Chunked, because this array is NOT governed by any batch-size setting: it is however
    /// many messages the handler published inside the caller's transaction. A handler that fans out to
    /// 350 recipients used to fail on SQL Server with "The incoming request has too many parameters",
    /// an error that mentions neither Wolverine nor message counts.
    /// </summary>
    /// <remarks>
    /// Every chunk runs on the CALLER'S transaction, so the batch is still all-or-nothing exactly as it
    /// was -- more commands, same atomicity.
    /// </remarks>
    public async Task StoreOutgoingAsync(DbTransaction tx, Envelope[] envelopes)
    {
        foreach (var chunk in chunkByParameterLimit(envelopes, DatabaseConstants.OutgoingParametersPerEnvelope,
                     DatabaseConstants.OutgoingSharedParameters))
        {
            await using var cmd = DatabasePersistence.BuildOutgoingStorageCommand(chunk.ToArray(),
                Durability.AssignedNodeNumber, this);
            cmd.Connection = tx.Connection;
            cmd.Transaction = tx;

            await cmd.ExecuteNonQueryAsync(_cancellation);
        }

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

        // GH-4375: chunk so a large batch cannot exceed the provider's parameter ceiling, and run the
        // chunks in one explicit transaction so the batch stays all-or-nothing. The coalescer that feeds
        // this falls back to storing each envelope individually when a batch fails, which is only safe
        // if a failed batch wrote nothing.
        var chunks = BuildsFixedArityBatches
            ? [new ArraySegment<Envelope>(array)]
            : chunkByParameterLimit(array, DatabaseConstants.OutgoingParametersPerEnvelope,
                DatabaseConstants.OutgoingSharedParameters).ToArray();

        await using var conn = await DataSource.OpenConnectionAsync(_cancellation);

        try
        {
            if (chunks.Length == 1)
            {
                await using var single = BuildBatchedOutgoingCommand(chunks[0].ToArray(), ownerId);
                single.Connection = conn;
                await single.ExecuteNonQueryAsync(_cancellation);
            }
            else
            {
                await using var tx = await conn.BeginTransactionAsync(_cancellation);
                try
                {
                    foreach (var chunk in chunks)
                    {
                        await using var command = BuildBatchedOutgoingCommand(chunk.ToArray(), ownerId);
                        command.Connection = conn;
                        command.Transaction = tx;
                        await command.ExecuteNonQueryAsync(_cancellation);
                    }

                    await tx.CommitAsync(_cancellation);
                }
                catch
                {
                    await tx.RollbackAsync(_cancellation);
                    throw;
                }
            }
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

    /// <summary>
    /// GH-4320. Virtual for the same reason as <c>BuildBatchedIncomingCommand</c>: the default emits one
    /// values-clause per envelope, so both the command text and the parameter count scale with the
    /// batch size.
    /// </summary>
    protected virtual DbCommand BuildBatchedOutgoingCommand(Envelope[] envelopes, int ownerId)
    {
        return DatabasePersistence.BuildOutgoingStorageCommand(envelopes, ownerId, this);
    }

    protected abstract string
        determineOutgoingEnvelopeSql(DurabilitySettings settings);
}