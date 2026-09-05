using Microsoft.Extensions.Logging;
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
    /// Gated on partitioning, so every non-partitioned store issues byte-for-byte the statement it always
    /// did. Whether a non-partitioned store should also refuse to reschedule a <c>Handled</c> row is a
    /// separate decision with its own consequence -- the <c>rowsAffected == 0</c> fallback below would then
    /// insert onto that row's primary key -- and #4216 asks for it to be made deliberately rather than as a
    /// side effect of an incident fix.
    /// </summary>
    protected string ScheduleExecutionSql()
    {
        var table = MarkAsHandledTableName;

        var set =
            $"update {table} set execution_time = @time, status = '{EnvelopeStatus.Scheduled}', attempts = @attempts, owner_id = {TransportConstants.AnyNode}";
        var identity = $"where id = @id and {DatabaseConstants.ReceivedAt} = @uri";

        if (!Durability.EnableInboxPartitioning)
        {
            return $"{set} {identity};";
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
            await StoreIncomingAsync(envelope);
        }
    }
}
