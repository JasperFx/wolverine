using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Weasel.Core;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    public abstract void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow);

    private string? _scheduleExecutionSql;

    /// <summary>
    /// GH-4216. The last two of the three sibling statements #4209 reproduced and deliberately left alone.
    /// Both matched the identity with no <c>status</c> predicate and then <em>set</em> <c>status</c>, which
    /// under <see cref="DurabilitySettings.EnableInboxPartitioning"/> is a cross-partition move -- and the
    /// match was wide enough to drag rows the caller never meant to touch along with it. Two ways to fail,
    /// both ending in a 23505 that rolls the reschedule back, and a reschedule that fails is a retry that
    /// never happens:
    ///
    /// <list type="number">
    /// <item>A retained <c>Handled</c> row shares the identity, so the statement moved the handled copy into
    /// the scheduled partition alongside the row it was actually given -- two rows landing on one scheduled
    /// key. Resurrecting a message that already completed is the worse half of that: the collision is what
    /// makes it visible.</item>
    /// <item>A <c>Scheduled</c> row already exists for the identity -- an earlier retry, which is exactly the
    /// state <c>RescheduleExistingEnvelopeForRetryAsync</c> exists to service -- so moving the incoming copy
    /// onto that key collides with it.</item>
    /// </list>
    ///
    /// Partitioning is what makes those pairs possible at all: the primary key gains the status column, so one
    /// identity can sit in two partitions at once. The resolution mirrors what the scheduled poller already
    /// does with such a pair and what #4224 did for mark-as-handled -- one row survives, the redundant copy is
    /// discarded, and the move is never attempted onto an occupied key. The survivor is the scheduled row,
    /// because that is the copy the poller will actually run.
    ///
    /// The existence check uses whichever identity the TABLE was keyed with, not the row-matching clause.
    /// Under <see cref="MessageIdentity.IdOnly"/> a copy at another destination is the SAME identity -- the
    /// key is <c>(id, status)</c> and <c>received_at</c> is not part of it -- so an existence check that also
    /// matched <c>received_at</c> would miss the scheduled row it is looking for and let the collision
    /// through anyway. Same distinction #4209's promotion fix and #4224's mark-as-handled fix both had to
    /// make.
    ///
    /// The delete-then-update pair is gated on partitioning, where the pairs exist. Refusing to reschedule a
    /// <c>Handled</c> row is not: every store refuses it (GH-4216, decided), and the <c>rowsAffected == 0</c>
    /// fallback below treats the resulting insert collision as "already handled, discard".
    /// </summary>
    protected string ScheduleExecutionSql()
    {
        var table = MarkAsHandledTableName;

        var set =
            $"update {table} set execution_time = @time, status = '{EnvelopeStatus.Scheduled}', attempts = @attempts, owner_id = {TransportConstants.AnyNode}";
        var identity = $"where id = @id and {DatabaseConstants.ReceivedAt} = @uri";

        // GH-4216, the decision the note below used to defer: a retry booking that lands AFTER the message was
        // marked handled must not resurrect it. ScheduleExecutionAsync runs from a RetryBlock in DurableReceiver,
        // so it is free to land after mark-as-handled for the same identity, and until now every non-partitioned
        // store then flipped the retained Handled row straight back to Scheduled with its body intact -- a second,
        // fully executable copy of a message that already completed. The Handled row is the dedup window's record
        // of completion and is nobody else's to touch, on any store.
        if (!Durability.EnableInboxPartitioning)
        {
            return $"{set} {identity} and {DatabaseConstants.Status} <> '{EnvelopeStatus.Handled}';";
        }

        var scheduledExists = Durability.MessageIdentity == MessageIdentity.IdOnly
            ? $"select 1 from {table} s where s.id = @id and s.{DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}'"
            : $"select 1 from {table} s where s.id = @id and s.{DatabaseConstants.ReceivedAt} = @uri and s.{DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}'";

        // Discard the redundant incoming copy, then update whatever is left. The delete names Incoming
        // exactly rather than "not Scheduled": a retained Handled row is the KeepAfterMessageHandling dedup
        // window and is nobody else's to remove.
        //
        // After it, at most ONE non-handled row can match -- either the scheduled row survives and is updated
        // in place, which is no partition move at all, or there was no scheduled row and the single incoming
        // row moves into the scheduled partition exactly as it always has. Excluding Handled from the update
        // is what keeps a retained handled copy out of the move, and out of a second execution.
        return
            $"delete from {table} where id = @id and {DatabaseConstants.ReceivedAt} = @uri " +
            $"and {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}' and exists ({scheduledExists});" +
            $"{set} {identity} and {DatabaseConstants.Status} <> '{EnvelopeStatus.Handled}';";
    }

    public Task ScheduleExecutionAsync(Envelope envelope)
    {
        Logger.LogDebug("Persisting envelope {EnvelopeId} ({MessageType}) as Scheduled in database inbox at {Destination}", envelope.Id, envelope.MessageType, envelope.Destination);

        _scheduleExecutionSql ??= ScheduleExecutionSql();

        return CreateCommand(_scheduleExecutionSql)
            .With("time", envelope.ScheduledTime!.Value)
            .With("attempts", envelope.Attempts)
            .With("id", envelope.Id)
            .With("uri", envelope.Destination!.ToString())
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        Logger.LogDebug("Rescheduling envelope {EnvelopeId} ({MessageType}) for retry in database inbox at {Destination}", envelope.Id, envelope.MessageType, envelope.Destination);
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;

        _scheduleExecutionSql ??= ScheduleExecutionSql();

        // Try UPDATE first so we don't collide with a row left by an earlier reschedule.
        // The same call services two scenarios:
        //   * UseDurableInbox — the inbox row was inserted on arrival (issue #2462).
        //   * ProcessInline   — retry #1 inserts, retry #2+ finds the previous Scheduled row
        //                       (issue #2823).
        // INSERT-only blew up on the existing row's primary key in both. When no row exists
        // (e.g. ProcessInline retry #1, or BufferedLocalQueue's scheduled-publish path),
        // UPDATE affects 0 rows and we fall back to StoreIncomingAsync.
        //
        // GH-4216: under partitioning the statement is a DELETE + UPDATE pair, and the count this reads is
        // the total across both. That is exactly what this fallback needs. When the delete discarded a
        // redundant copy in favour of a scheduled row that already exists -- which under
        // MessageIdentity.IdOnly can be a row at another destination, so the UPDATE itself matches nothing --
        // the retry IS parked, and inserting on top of it would be the 23505 this fix exists to prevent.
        var rowsAffected = await CreateCommand(_scheduleExecutionSql)
            .With("time", envelope.ScheduledTime!.Value)
            .With("attempts", envelope.Attempts)
            .With("id", envelope.Id)
            .With("uri", envelope.Destination!.ToString())
            .ExecuteNonQueryAsync(_cancellation);

        if (rowsAffected == 0)
        {
            // GH-4216. The update matched nothing. Either there is no row at all, or the only row for this
            // identity is a retained Handled one -- the message already completed, and a retry booked after
            // the fact is discarded rather than executed a second time. Asked explicitly rather than left to
            // the insert's primary key: under inbox partitioning the key includes the status, so a Scheduled
            // row would sit beside the Handled one without colliding, and the message would run again.
            if (await handledRowExistsAsync(envelope))
            {
                Logger.LogDebug(
                    "Discarding the retry of envelope {EnvelopeId} ({MessageType}) at {Destination}: it was already marked as handled",
                    envelope.Id, envelope.MessageType, envelope.Destination);
                return;
            }

            try
            {
                await StoreIncomingAsync(envelope);
            }
            catch (DuplicateIncomingEnvelopeException)
            {
                // The mark-as-handled for this identity landed between the check above and the insert
                Logger.LogDebug(
                    "Discarding the retry of envelope {EnvelopeId} ({MessageType}) at {Destination}: it was already marked as handled",
                    envelope.Id, envelope.MessageType, envelope.Destination);
            }
        }
    }

    private string? _handledRowExistsSql;

    /// <summary>
    /// Whether a retained Handled row exists for the envelope's identity. The same identity rule the
    /// mark-as-handled and promotion statements use: under <see cref="MessageIdentity.IdOnly"/> a copy at
    /// another destination is the SAME identity, so the destination is not part of the match
    /// </summary>
    private async Task<bool> handledRowExistsAsync(Envelope envelope)
    {
        _handledRowExistsSql ??= Durability.MessageIdentity == MessageIdentity.IdOnly
            ? $"select count(*) from {MarkAsHandledTableName} where id = @id and {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}'"
            : $"select count(*) from {MarkAsHandledTableName} where id = @id and {DatabaseConstants.ReceivedAt} = @uri and {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}'";

        var command = CreateCommand(_handledRowExistsSql).With("id", envelope.Id);
        if (Durability.MessageIdentity != MessageIdentity.IdOnly)
        {
            command = command.With("uri", envelope.Destination!.ToString());
        }

        var count = await command.ExecuteScalarAsync(_cancellation);
        return Convert.ToInt64(count) > 0;
    }
}
