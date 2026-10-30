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

        // GH-5022: a node claiming rows restarts their stale clock; releasing them to any node does not need to
        var restartClock = ownerId == TransportConstants.AnyNode ? string.Empty : restartIncomingStaleClock;

        var builder = ToCommandBuilder();
        foreach (var envelope in incoming)
        {
            builder.Append($"update {QuotedTableNameFor(DatabaseConstants.IncomingTable)} set owner_id = ");
            builder.AppendParameter(ownerId);
            builder.Append(restartClock);
            builder.Append($" where {DatabaseConstants.Id} = ");
            builder.AppendParameter(envelope.Id);
            builder.Append($" and {DatabaseConstants.ReceivedAt} = ");
            builder.AppendParameter(envelope.Destination!.ToString(), AddressParameterType);
            builder.Append(";");
        }

        return executeCommandBatch(builder, _cancellation);
    }

    /// <summary>
    /// GH-5073. Claims only incoming rows that are still globally owned, and returns only rows this node won.
    /// Each conditional UPDATE is atomic; the transaction keeps a partially claimed page from being stranded
    /// if a later update in the page fails. Rows are claimed in <see cref="DatabasePersistence.InClaimOrder"/>
    /// so two competing claims cannot deadlock on each other's row locks.
    /// </summary>
    public virtual async Task<IReadOnlyList<Envelope>> TryClaimIncomingAsync(int ownerId,
        IReadOnlyList<Envelope> incoming)
    {
        if (incoming.Count == 0) return Array.Empty<Envelope>();

        var claimed = new List<Envelope>(incoming.Count);
        var restartClock = restartIncomingStaleClock;
        await using var conn = await DataSource.OpenConnectionAsync(_cancellation);
        await using var tx = await conn.BeginTransactionAsync(_cancellation);

        foreach (var envelope in DatabasePersistence.InClaimOrder(incoming))
        {
            await using var cmd = conn.CreateCommand(
                $"update {QuotedTableNameFor(DatabaseConstants.IncomingTable)} " +
                $"set {DatabaseConstants.OwnerId} = @owner{restartClock} " +
                $"where id = @id and {DatabaseConstants.ReceivedAt} = @destination " +
                $"and {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} " +
                $"and {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}'");
            cmd.Transaction = tx;
            cmd.With("owner", ownerId)
                .With("id", envelope.Id)
                .With("destination", envelope.Destination!.ToString(), AddressParameterType);

            if (await cmd.ExecuteNonQueryAsync(_cancellation) > 0)
            {
                envelope.OwnerId = ownerId;
                claimed.Add(envelope);
            }
        }

        await tx.CommitAsync(_cancellation);
        return claimed;
    }

    /// <summary>
    /// GH-4375. Chunked for the same reason as the outgoing transactional path: this array is sized by
    /// the caller's transaction rather than by any batch-size setting, and 234 envelopes was enough to
    /// exceed SQL Server's 2,100-parameter ceiling.
    /// </summary>
    /// <remarks>
    /// Chunks run on the CALLER'S transaction, so a duplicate in a later chunk still rolls the whole
    /// thing back with the caller's other work -- the atomicity this had before, at more commands. The
    /// exception still reports the whole batch, because a rolled-back multi-row insert cannot say which
    /// row collided.
    /// </remarks>
    public async Task StoreIncomingAsync(DbTransaction tx, Envelope[] envelopes)
    {
        var refusesHandled = incomingInsertRefusesHandled;

        foreach (var chunk in chunkByParameterLimit(envelopes, DatabaseConstants.IncomingParametersPerEnvelope))
        {
            await using var cmd = refusesHandled
                ? BuildBatchedIncomingCommand(chunk)
                : DatabasePersistence.BuildIncomingStorageCommand(chunk.ToArray(), this);

            cmd.Transaction = tx;
            cmd.Connection = tx.Connection;

            int inserted;
            try
            {
                inserted = await cmd.ExecuteNonQueryAsync(_cancellation);
            }
            catch (Exception e) when (IsDuplicateEnvelopeException(e))
            {
                throw new DuplicateIncomingEnvelopeException(envelopes);
            }

            // A refused row leaves the caller's transaction usable, where a key violation would have aborted it.
            // The exception is what keeps it from committing, together with the caller's other work
            if (refusesHandled && inserted < chunk.Count)
            {
                throw new DuplicateIncomingEnvelopeException(envelopes);
            }
        }
    }

    public async Task DeleteIncomingEnvelopeAsync(Envelope envelope)
    {
        if (HasDisposed) return;

        // Same identity clause the dead letter move uses to retire the row it is replacing.
        var builder = ToCommandBuilder();
        builder.Append($"delete from {QuotedTableNameFor(DatabaseConstants.IncomingTable)} WHERE id = ");
        builder.AppendParameter(envelope.Id);
        builder.Append($" and {DatabaseConstants.ReceivedAt} = ");
        builder.AppendParameter(envelope.Destination!.ToString(), AddressParameterType);
        builder.Append(';');

        await executeCommandBatch(builder, _cancellation);
    }

    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        // GH-5116. Never return quietly here: callers settle the message with the broker once this returns, so a
        // dead letter "written" to a disposed store would be completed and lost
        if (HasDisposed)
        {
            throw new ObjectDisposedException(GetType().FullName,
                $"The message store is disposed, so envelope {envelope.Id} could not be moved to dead letter storage");
        }

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
            builder.AppendParameter(envelope.Destination!.ToString(), AddressParameterType);
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
        return buildMarkAsHandled(idExpression, uriExpression, "@keepUntil", null, default).Sql;
    }

    /// <summary>
    /// GH-4705. The mark-as-handled statement in a form a store can queue into somebody <i>else's</i> batch --
    /// Marten's <c>QueueSqlCommand</c>, Polecat's and Fisher's <c>ITransactionParticipant</c> -- rather than
    /// execute on a connection of its own. Those three each hand-wrote <c>... where id = ?</c>, which is both
    /// GH-4701 failure modes (every destination's copy of a fanned-out message retired at once, and a
    /// redelivered row that cannot be retired at all) on stores that never went through this class.
    ///
    /// <para>
    /// Drivers that bind by NAME pass their own placeholders -- <c>"@id"</c>, <c>"@uri"</c>,
    /// <c>"@keepUntil"</c> -- and ignore <see cref="MarkAsHandledCommand.Arguments" />. Drivers that bind by
    /// POSITION pass the same marker for all three (<c>"?"</c>) and bind <c>Arguments</c> in the order given.
    /// The order is produced here, beside the branches that decide it, precisely so that a caller never has to
    /// mirror it: under inbox partitioning the id and destination each appear three times, and how many times
    /// depends on <see cref="MessageIdentity" />.
    /// </para>
    /// </summary>
    public MarkAsHandledCommand BuildMarkIncomingAsHandled(Envelope envelope, DateTimeOffset keepUntil,
        string idExpression, string uriExpression, string keepUntilExpression)
    {
        return buildMarkAsHandled(idExpression, uriExpression, keepUntilExpression, envelope, keepUntil);
    }

    private MarkAsHandledCommand buildMarkAsHandled(string idExpression, string uriExpression,
        string keepUntilExpression, Envelope? envelope, DateTimeOffset keepUntil)
    {
        var table = MarkAsHandledTableName;

        // Collected in placeholder order as each statement is composed. Null envelope means a name-binding
        // caller that only wants the SQL, so nothing is accumulated.
        var collecting = envelope != null;
        object id() => envelope!.Id;
        object uri() => envelope!.Destination!.ToString();

        // GH-4739: a Handled row is kept for idempotency only and nothing recovers it, so it gives up its owner --
        // as a row inserted already Handled does (Envelope.ForPersistedHandled). Left owned, every later release
        // of that node's ownership rewrote the retained rows, and they filled the owner index the releases seek.
        var update =
            $"update {table} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = {keepUntilExpression}, {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} where id = {idExpression} and {DatabaseConstants.ReceivedAt} = {uriExpression}";

        if (!Durability.EnableInboxPartitioning)
        {
            return new MarkAsHandledCommand(update,
                collecting ? [keepUntil, id(), uri()] : []);
        }

        // The existence check has to use whichever identity the TABLE was keyed with, not the row-matching
        // clause above. Under IdOnly a redelivery at a different destination is the SAME identity -- the key is
        // (id, status) and received_at is not part of it -- so an existence check that also matched received_at
        // would miss the handled row it is looking for and let the collision through anyway.
        var identityIsIdOnly = Durability.MessageIdentity == MessageIdentity.IdOnly;

        var handledExists = identityIsIdOnly
            ? $"select 1 from {table} h where h.id = {idExpression} and h.{DatabaseConstants.Status} = '{EnvelopeStatus.Handled}'"
            : $"select 1 from {table} h where h.id = {idExpression} and h.{DatabaseConstants.ReceivedAt} = {uriExpression} and h.{DatabaseConstants.Status} = '{EnvelopeStatus.Handled}'";

        var delete =
            $"delete from {table} where id = {idExpression} and {DatabaseConstants.ReceivedAt} = {uriExpression} and {DatabaseConstants.Status} <> '{EnvelopeStatus.Handled}' " +
            $"and exists ({handledExists})";

        // GH-4736: two statements, kept apart. Joining them with ';' and handing the result to a caller as one
        // string is what broke Marten: QueueSqlCommand refuses any SQL containing a ';', so every durable-inbox
        // message committing through a Marten session was dead-lettered. MarkAsHandledCommand still exposes the
        // joined form for the callers that put it straight on an ADO CommandText.
        object[] deleteArguments = [];
        object[] updateArguments = [];

        if (collecting)
        {
            // delete ... where id = ? and received_at = ? ... and exists (... h.id = ? [and h.received_at = ?])
            deleteArguments = identityIsIdOnly
                ? [id(), uri(), id()]
                : [id(), uri(), id(), uri()];

            // update ... keep_until = ? where id = ? and received_at = ?
            updateArguments = [keepUntil, id(), uri()];
        }

        return new MarkAsHandledCommand(
            new MarkAsHandledStatement(delete, deleteArguments),
            new MarkAsHandledStatement($"{update} and {DatabaseConstants.Status} <> '{EnvelopeStatus.Handled}'",
                updateArguments));
    }

    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        if (HasDisposed) return Task.CompletedTask;
        var keepUntil = DateTimeOffset.UtcNow.Add(Durability.KeepAfterMessageHandling);
        _markEnvelopeAsHandledById ??= MarkAsHandledSql("@id", "@uri");

        return CreateCommand(_markEnvelopeAsHandledById)
            .With("id", envelope.Id)
            .With("keepUntil", keepUntil)
            .With("uri", envelope.Destination!.ToString(), AddressParameterType)
            .ExecuteNonQueryAsync(_cancellation);
    }

    /// <summary>
    /// The in-transaction twin of <see cref="MarkIncomingEnvelopeAsHandledAsync(Envelope)"/>, used by
    /// <c>EfCoreEnvelopeTransaction.CommitAsync</c> to close out a durable-inbox message inside the application's own
    /// transaction. It runs the very same statement, so it matches the whole identity -- <c>received_at</c> as well as
    /// <c>id</c> -- and carries the partition-aware shape. The interface default matched on the id alone, which under
    /// <see cref="MessageIdentity.IdAndDestination"/> marked every destination's copy of a fanned-out message as
    /// handled, including copies whose own handler had not run yet.
    /// </summary>
    public async Task MarkIncomingEnvelopeAsHandledInTransactionAsync(DbConnection conn, DbTransaction? tx,
        Envelope envelope, DateTimeOffset keepUntil, CancellationToken cancellation)
    {
        _markEnvelopeAsHandledById ??= MarkAsHandledSql("@id", "@uri");

        await using var cmd = conn.CreateCommand(_markEnvelopeAsHandledById)
            .With("id", envelope.Id)
            .With("keepUntil", keepUntil)
            .With("uri", envelope.Destination!.ToString(), AddressParameterType);
        cmd.Transaction = tx;

        await cmd.ExecuteNonQueryAsync(cancellation);
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
                builder.AppendParameter(uri, AddressParameterType);
                builder.Append(
                    $" and {DatabaseConstants.Status} <> '{EnvelopeStatus.Handled}' and exists (select 1 from {table} h where h.id = ");
                builder.AppendParameter(envelope.Id);

                // Same identity rule as MarkAsHandledSql: under IdOnly a redelivery at another destination is
                // the same identity, so matching received_at here would miss the handled row it looks for.
                if (Durability.MessageIdentity != MessageIdentity.IdOnly)
                {
                    builder.Append($" and h.{DatabaseConstants.ReceivedAt} = ");
                    builder.AppendParameter(uri, AddressParameterType);
                }

                builder.Append($" and h.{DatabaseConstants.Status} = '{EnvelopeStatus.Handled}');");
            }

            builder.Append($"update {table} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = @keepUntil, {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} where id = ");
            builder.AppendParameter(envelope.Id);
            builder.Append(" and ");
            builder.Append(DatabaseConstants.ReceivedAt);
            builder.Append( " = ");
            builder.AppendParameter(uri, AddressParameterType);

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
            .With("uri", envelope.Destination!.ToString(), AddressParameterType)
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task StoreIncomingAsync(Envelope envelope)
    {
        if (HasDisposed) return;

        var refusesHandled = incomingInsertRefusesHandled;

        await using var cmd = refusesHandled
            ? BuildBatchedIncomingCommand([envelope])
            : buildIncomingCommand(envelope);

        int inserted;
        try
        {
            await using var conn = await DataSource.OpenConnectionAsync(_cancellation);
            try
            {
                cmd.Connection = conn;
                inserted = await cmd.ExecuteNonQueryAsync(_cancellation).ConfigureAwait(false);
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

        if (refusesHandled && inserted == 0)
        {
            throw new DuplicateIncomingEnvelopeException(envelope);
        }
    }

    private DbCommand buildIncomingCommand(Envelope envelope)
    {
        var builder = ToCommandBuilder();
        DatabasePersistence.BuildIncomingStorageCommand(this, builder, envelope);
        return builder.Compile();
    }

    /// <summary>
    /// True when this store's inbox INSERT refuses an envelope by inserting nothing, rather than by raising a key
    /// violation, so a short row count is how the duplicate shows. Such a store builds every inbox insert -- one
    /// envelope or a batch, on its own connection or on the caller's transaction -- with
    /// <see cref="BuildBatchedIncomingCommand" />, which is the statement that carries the refusal, and every caller
    /// still gets the <see cref="DuplicateIncomingEnvelopeException" /> a key violation produces.
    ///
    /// <para>
    /// PostgreSQL sets it under <see cref="DurabilitySettings.EnableInboxPartitioning" />. The incoming table is
    /// then PARTITION BY LIST (status) with status in the primary key, so a Handled row retained for
    /// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> -- the row that exists to refuse a broker
    /// redelivery -- does not collide with an insert of its identity, and the redelivery was stored and handled a
    /// second time. The insert refuses an identity the handled partition holds instead.
    /// </para>
    /// </summary>
    protected virtual bool incomingInsertRefusesHandled => false;

    /// <summary>
    /// GH-4320. The batched inbox INSERT, as one command. Virtual because the default shape -- one
    /// <c>insert ... values (@p0..@p8);</c> per envelope -- makes both the command text and the
    /// parameter count scale with the batch size. A provider that can express the batch at FIXED arity
    /// overrides this; PostgreSQL does, with <c>unnest</c>, and measures ~1.35x faster on the insert.
    /// The saving is the smaller command and the parameter count, not plan caching -- see the note on
    /// PostgresqlMessageStore.BatchedInserts for why the plan-cache premise did not survive measurement.
    /// </summary>
    protected virtual DbCommand BuildBatchedIncomingCommand(IReadOnlyList<Envelope> envelopes)
    {
        return DatabasePersistence.BuildIncomingStorageCommand(envelopes, this);
    }

    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        if (envelopes.Count == 0) return;

        // GH-4375: this one already runs in an explicit transaction (see below), so chunking it keeps
        // the all-or-nothing guarantee the duplicate handling below depends on. A provider that
        // overrides BuildBatchedIncomingCommand with a fixed-arity form -- PostgreSQL, since GH-4320 --
        // has no ceiling to hit and gets a single chunk regardless.
        var chunks = BuildsFixedArityBatches
            ? [new ArraySegment<Envelope>(envelopes as Envelope[] ?? envelopes.ToArray())]
            : chunkByParameterLimit(envelopes as Envelope[] ?? envelopes.ToArray(),
                DatabaseConstants.IncomingParametersPerEnvelope).ToArray();

        var refused = false;
        var duplicateKey = false;

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
                var inserted = 0;
                foreach (var chunk in chunks)
                {
                    await using var cmd = BuildBatchedIncomingCommand(chunk);
                    cmd.Connection = conn;
                    cmd.Transaction = tx;
                    inserted += await cmd.ExecuteNonQueryAsync(_cancellation);
                }

                // The insert left out an envelope whose identity is already handled. Rolled back like a key
                // violation below, so that a refused batch is still one where nothing landed: the receiver and
                // the store coalescer re-run every envelope of it one at a time
                refused = incomingInsertRefusesHandled && inserted < envelopes.Count;
                if (refused)
                {
                    await tx.RollbackAsync(_cancellation);
                }
                else
                {
                    await tx.CommitAsync(_cancellation);
                }
            }
            catch (Exception e) when (IsDuplicateEnvelopeException(e))
            {
                await tx.RollbackAsync(_cancellation);
                duplicateKey = true;
            }
        }
        finally
        {
            await conn.CloseAsync();
        }

        // The lookups below each open a connection of their own, so the batch's goes back to the pool first:
        // holding it while they wait for another hangs until the open times out when the pool has one left
        if (duplicateKey)
        {
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

            // GH-5123. A duplicate-key error with no matching row means the duplicate is INSIDE the batch:
            // two copies of one message in the same micro-batch, which is what a broker redelivery plus an
            // outbox resend after an outage looks like. That is still a duplicate, and rethrowing the raw
            // provider exception made the receiver pause the listener for an "unavailable" inbox (and
            // MultiTenantedMessageStore trip the tenant's write brake). Report the whole batch, the way
            // RavenDB does: the transaction rolled back, so nothing landed, and the receiver's per-envelope
            // path stores the first copy and settles the second as the duplicate it is.
            //
            // Deduplicating the batch here before the insert would be wrong rather than just slower: the
            // caller marks every envelope it passed as persisted and hands all of them to the handlers, so
            // the copy dropped here would run anyway.
            throw new DuplicateIncomingEnvelopeException(duplicates.Count > 0 ? duplicates : envelopes);
        }

        if (refused)
        {
            throw new DuplicateIncomingEnvelopeException(await alreadyHandledAsync(envelopes));
        }
    }

    /// <summary>
    /// The envelopes of a refused batch whose identity is already handled, which is the only reason the insert
    /// leaves one out. Asked of the handled rows rather than through <see cref="ExistsAsync" />, which would also
    /// report an identity that only sits in the scheduled partition -- one the insert accepted
    /// </summary>
    private async Task<IReadOnlyList<Envelope>> alreadyHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        var handled = new List<Envelope>();
        foreach (var envelope in envelopes)
        {
            if (await handledRowExistsAsync(envelope, _cancellation).ConfigureAwait(false))
            {
                handled.Add(envelope);
            }
        }

        // A handled row purged since the insert leaves nothing to point at. The batch rolled back, so report all of
        // it, as GH-5123 does, and let the receiver's one-at-a-time path settle each envelope
        return handled.Count > 0 ? handled : envelopes;
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
