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

        // Attempt UPDATE first.
        var rowsAffected = await CreateCommand(
                $"update {QuotedSchemaName}.{DatabaseConstants.IncomingTable} " +
                $"set execution_time = @time, status = '{EnvelopeStatus.Scheduled}', attempts = @attempts, owner_id = {TransportConstants.AnyNode} " +
                $"where id = @id and {DatabaseConstants.ReceivedAt} = @uri;")
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