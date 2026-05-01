using System.Data.Common;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Interop;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    protected string _markEnvelopeAsHandledById;
    protected string _incrementIncomingEnvelopeAttempts;

    public abstract Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit);

    public Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        if (incoming.Count == 0)
            return Task.CompletedTask;

        var builder = ToCommandBuilder();
        foreach (var envelope in incoming)
        {
            builder.Append($"update {QuotedSchemaName}.{DatabaseConstants.IncomingTable} set owner_id = ");
            builder.AppendParameter(ownerId);
            builder.Append($" where {DatabaseConstants.Id} = ");
            builder.AppendParameter(envelope.Id);
            builder.Append($" and {DatabaseConstants.ReceivedAt} = ");
            builder.AppendParameter(envelope.Destination!.ToString());
            builder.Append(";");
        }

        return executeCommandBatch(builder, _cancellation);
    }

    public async Task StoreIncomingAsync(DbTransaction tx, Envelope[] envelopes)
    {
        var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, this);

        cmd.Transaction = tx;
        cmd.Connection = tx.Connection;

        try
        {
            await cmd.ExecuteNonQueryAsync(_cancellation);
        }
        catch (Exception e) when (IsDuplicateEnvelopeException(e))
        {
            throw new DuplicateIncomingEnvelopeException(envelopes);
        }
    }

    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        if (HasDisposed) return;

        if (Durability.DeadLetterQueueExpirationEnabled && envelope.DeliverBy == null)
        {
            envelope.DeliverBy = DateTimeOffset.UtcNow.Add(Durability.DeadLetterQueueExpiration);
        }

        try
        {
            var builder = ToCommandBuilder();
            builder.Append($"delete from {QuotedSchemaName}.{DatabaseConstants.IncomingTable} WHERE id = ");
            builder.AppendParameter(envelope.Id);
            builder.Append($" and {DatabaseConstants.ReceivedAt} = ");
            builder.AppendParameter(envelope.Destination!.ToString());
            builder.Append(';');

            DatabasePersistence.ConfigureDeadLetterCommands(Durability, envelope, exception, builder, this);

            await executeCommandBatch(builder, _cancellation);
        }
        catch (Exception e)
        {
            if (IsDuplicateEnvelopeException(e)) return;
            throw;
        }
    }

    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        if (HasDisposed) return Task.CompletedTask;
        var keepUntil = DateTimeOffset.UtcNow.Add(Durability.KeepAfterMessageHandling);
        return CreateCommand(_markEnvelopeAsHandledById)
            .With("id", envelope.Id)
            .With("keepUntil", keepUntil)
            .With("uri", envelope.Destination!.ToString())
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        if (HasDisposed) return;
        var keepUntil = DateTimeOffset.UtcNow.Add(Durability.KeepAfterMessageHandling);

        var builder = ToCommandBuilder();
        builder.AddNamedParameter("keepUntil", keepUntil);

        foreach (var envelope in envelopes)
        {
            builder.Append($"update {QuotedSchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = @keepUntil where id = ");
            builder.AppendParameter(envelope.Id);
            builder.Append(" and ");
            builder.Append(DatabaseConstants.ReceivedAt);
            builder.Append( " = ");
            builder.AppendParameter(envelope.Destination!.ToString());
            builder.Append(";");
        }

        await executeCommandBatch(builder, _cancellation);
    }

    private async Task executeCommandBatch(DbCommandBuilder builder, CancellationToken token)
    {
        var cmd = builder.Compile();

        await using var conn = await DataSource.OpenConnectionAsync(token);
        try
        {
            cmd.Connection = conn;
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        if (HasDisposed) return Task.CompletedTask;
        return CreateCommand(_incrementIncomingEnvelopeAttempts)
            .With("attempts", envelope.Attempts)
            .With("id", envelope.Id)
            .With("uri", envelope.Destination!.ToString())
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task StoreIncomingAsync(Envelope envelope)
    {
        if (HasDisposed) return;

        var builder = ToCommandBuilder();
        DatabasePersistence.BuildIncomingStorageCommand(this, builder, envelope);

        var cmd = builder.Compile();
        try
        {
            await using var conn = await DataSource.OpenConnectionAsync(_cancellation);
            try
            {
                cmd.Connection = conn;
                await cmd.ExecuteNonQueryAsync(_cancellation).ConfigureAwait(false);
            }
            finally
            {
                await conn.CloseAsync();
            }
        }
        catch (Exception e)
        {
            if (IsDuplicateEnvelopeException(e))
            {
                throw new DuplicateIncomingEnvelopeException(envelope);
            }

            throw;
        }
    }

    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        if (envelopes.Count == 0) return;

        var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, this);

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        try
        {
            // Wrap the multi-statement batch in an explicit transaction so the
            // semantics are uniform across drivers: SqlClient/MySqlConnector/
            // Microsoft.Data.Sqlite autocommit per statement otherwise, which
            // would partially persist the batch on a duplicate-key failure and
            // leave the inbox in a state that is indistinguishable from
            // "envelope was already there". Npgsql already does this implicitly,
            // but being explicit costs nothing and removes a per-driver footgun.
            await using var tx = await conn.BeginTransactionAsync(_cancellation);
            try
            {
                cmd.Connection = conn;
                cmd.Transaction = tx;
                await cmd.ExecuteNonQueryAsync(_cancellation);
                await tx.CommitAsync(_cancellation);
            }
            catch (Exception e) when (IsDuplicateEnvelopeException(e))
            {
                await tx.RollbackAsync(_cancellation);

                // Now that the batch is guaranteed rolled back, identify exactly
                // which envelopes were already present via id-existence. Callers
                // can retry the rest per-envelope.
                var duplicates = new List<Envelope>();
                foreach (var envelope in envelopes)
                {
                    if (await ExistsAsync(envelope, _cancellation).ConfigureAwait(false))
                    {
                        duplicates.Add(envelope);
                    }
                }

                if (duplicates.Count == 0)
                {
                    // Backend reported a duplicate-key error but no envelope id
                    // matches an existing row. Surface the original failure
                    // rather than silently swallowing it.
                    throw;
                }

                throw new DuplicateIncomingEnvelopeException(duplicates);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    protected bool IsDuplicateEnvelopeException(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (isExceptionFromDuplicateEnvelope(current)) return true;
        }

        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                if (IsDuplicateEnvelopeException(inner)) return true;
            }
        }

        return false;
    }

    protected abstract bool isExceptionFromDuplicateEnvelope(Exception ex);
}