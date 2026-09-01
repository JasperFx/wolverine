using System.Data.Common;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Interop;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    protected string? _markEnvelopeAsHandledById;
    protected string _incrementIncomingEnvelopeAttempts;

    public abstract Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit);

    public Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        if (incoming.Count == 0)
            return Task.CompletedTask;

        var builder = ToCommandBuilder();
        foreach (var envelope in incoming)
        {
            builder.Append($"update {QuotedTableNameFor(DatabaseConstants.IncomingTable)} set owner_id = ");
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
        await using var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, this);

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
            builder.Append($"delete from {QuotedTableNameFor(DatabaseConstants.IncomingTable)} WHERE id = ");
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

    /// <summary>
    /// GH-4216. The mark-as-handled statement for one identity. Unchanged without inbox partitioning: a single
    /// UPDATE flipping status to Handled.
    ///
    /// <para>
    /// With <c>EnableInboxPartitioning</c> the incoming table is PARTITION BY LIST (status) and status is part
    /// of the primary key, so uniqueness is only enforced per partition and one identity can legally sit in
    /// the incoming partition and the handled partition at once. Flipping status is then a cross-partition
    /// move -- DELETE + INSERT -- straight onto a key the handled partition already holds, and it fails. The
    /// consequence is worse than a failed update: a promoted or redelivered row could not be retired AT ALL,
    /// so it stayed Incoming, owned by the node that had already processed it, with no way forward.
    /// </para>
    ///
    /// <para>
    /// So when a Handled row already exists for the identity, the incoming row is DELETED rather than flipped.
    /// The retained Handled row is what serves <c>KeepAfterMessageHandling</c>'s dedup window, and it already
    /// exists -- keeping the second copy would add nothing and re-create the collision on the next attempt.
    /// The UPDATE that follows carries the same <c>status &lt;&gt; 'Handled'</c> predicate, so it affects zero
    /// rows when the DELETE handled it.
    /// </para>
    /// </summary>
    /// <summary>
    /// GH-4216. The incoming table as this provider spells it. Overridable because PostgreSQL quotes its
    /// schema name differently and used to rebuild the whole statement in its constructor to get it -- which
    /// meant the ONE provider that supports inbox partitioning was also the one bypassing the partition-aware
    /// statement entirely, and doing it in a constructor where EnableInboxPartitioning is not yet settled.
    /// Overriding just the name keeps the SQL in one place and lets it be built lazily, at call time.
    /// </summary>
    protected virtual string MarkAsHandledTableName => QuotedTableNameFor(DatabaseConstants.IncomingTable);

    protected string MarkAsHandledSql(string idExpression, string uriExpression)
    {
        var table = MarkAsHandledTableName;

        var update =
            $"update {table} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = @keepUntil where id = {idExpression} and {DatabaseConstants.ReceivedAt} = {uriExpression}";

        if (!Durability.EnableInboxPartitioning)
        {
            return update;
        }

        // The existence check has to use whichever identity the TABLE was keyed with, not the row-matching
        // clause above. Under IdOnly a redelivery at a different destination is the SAME identity -- the key is
        // (id, status) and received_at is not part of it -- so an existence check that also matched received_at
        // would miss the handled row it is looking for and let the collision through anyway.
        var handledExists = Durability.MessageIdentity == MessageIdentity.IdOnly
            ? $"select 1 from {table} h where h.id = {idExpression} and h.{DatabaseConstants.Status} = '{EnvelopeStatus.Handled}'"
            : $"select 1 from {table} h where h.id = {idExpression} and h.{DatabaseConstants.ReceivedAt} = {uriExpression} and h.{DatabaseConstants.Status} = '{EnvelopeStatus.Handled}'";

        return
            $"delete from {table} where id = {idExpression} and {DatabaseConstants.ReceivedAt} = {uriExpression} and {DatabaseConstants.Status} <> '{EnvelopeStatus.Handled}' " +
            $"and exists ({handledExists});" +
            $"{update} and {DatabaseConstants.Status} <> '{EnvelopeStatus.Handled}'";
    }

    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        if (HasDisposed) return Task.CompletedTask;
        var keepUntil = DateTimeOffset.UtcNow.Add(Durability.KeepAfterMessageHandling);
        _markEnvelopeAsHandledById ??= MarkAsHandledSql("@id", "@uri");

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

        // GH-4216: the batch has to carry the same partition-aware shape as the single-envelope path, or a
        // coalesced mark-as-handled strands exactly the rows the single one now retires. Each identity's
        // parameters are appended per occurrence, which is why the same value goes in more than once.
        var partitioned = Durability.EnableInboxPartitioning;
        var table = MarkAsHandledTableName;

        foreach (var envelope in envelopes)
        {
            var uri = envelope.Destination!.ToString();

            if (partitioned)
            {
                builder.Append($"delete from {table} where id = ");
                builder.AppendParameter(envelope.Id);
                builder.Append($" and {DatabaseConstants.ReceivedAt} = ");
                builder.AppendParameter(uri);
                builder.Append(
                    $" and {DatabaseConstants.Status} <> '{EnvelopeStatus.Handled}' and exists (select 1 from {table} h where h.id = ");
                builder.AppendParameter(envelope.Id);

                // Same identity rule as MarkAsHandledSql: under IdOnly a redelivery at another destination is
                // the same identity, so matching received_at here would miss the handled row it looks for.
                if (Durability.MessageIdentity != MessageIdentity.IdOnly)
                {
                    builder.Append($" and h.{DatabaseConstants.ReceivedAt} = ");
                    builder.AppendParameter(uri);
                }

                builder.Append($" and h.{DatabaseConstants.Status} = '{EnvelopeStatus.Handled}');");
            }

            builder.Append($"update {table} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = @keepUntil where id = ");
            builder.AppendParameter(envelope.Id);
            builder.Append(" and ");
            builder.Append(DatabaseConstants.ReceivedAt);
            builder.Append( " = ");
            builder.AppendParameter(uri);

            if (partitioned)
            {
                builder.Append($" and {DatabaseConstants.Status} <> '{EnvelopeStatus.Handled}'");
            }

            builder.Append(";");
        }

        await executeCommandBatch(builder, _cancellation);
    }

    private async Task executeCommandBatch(DbCommandBuilder builder, CancellationToken token)
    {
        await using var cmd = builder.Compile();

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

        await using var cmd = builder.Compile();
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

        await using var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, this);

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

    /// <summary>
    /// Internal alias on top of <see cref="IsDuplicateEnvelopeException"/> for use by
    /// other components in this assembly that need cross-provider unique-constraint
    /// violation detection on inserts they own (e.g. the dynamic listener registry).
    /// The underlying exception shape is the same regardless of which table fired
    /// the unique constraint, so the per-provider <c>isExceptionFromDuplicateEnvelope</c>
    /// override is reused as-is.
    /// </summary>
    internal bool IsUniqueConstraintViolation(Exception ex) => IsDuplicateEnvelopeException(ex);

    protected abstract bool isExceptionFromDuplicateEnvelope(Exception ex);
}