using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    public abstract void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow);

    public Task ScheduleExecutionAsync(Envelope envelope)
    {
        Logger.LogDebug("Persisting envelope {EnvelopeId} ({MessageType}) as Scheduled in database inbox at {Destination}", envelope.Id, envelope.MessageType, envelope.Destination);
        return CreateCommand(
                $"update {QuotedSchemaName}.{DatabaseConstants.IncomingTable} set execution_time = @time, status = \'{EnvelopeStatus.Scheduled}\', attempts = @attempts, owner_id = {TransportConstants.AnyNode} where id = @id and {DatabaseConstants.ReceivedAt} = @uri;")
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

        // Try UPDATE first so we don't collide with a row left by an earlier reschedule.
        // The same call services two scenarios:
        //   * UseDurableInbox — the inbox row was inserted on arrival (issue #2462).
        //   * ProcessInline   — retry #1 inserts, retry #2+ finds the previous Scheduled row
        //                       (issue #2823).
        // INSERT-only blew up on the existing row's primary key in both. When no row exists
        // (e.g. ProcessInline retry #1, or BufferedLocalQueue's scheduled-publish path),
        // UPDATE affects 0 rows and we fall back to StoreIncomingAsync.
        var rowsAffected = await CreateCommand(
                $"update {QuotedSchemaName}.{DatabaseConstants.IncomingTable} set execution_time = @time, status = \'{EnvelopeStatus.Scheduled}\', attempts = @attempts, owner_id = {TransportConstants.AnyNode} where id = @id and {DatabaseConstants.ReceivedAt} = @uri;")
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