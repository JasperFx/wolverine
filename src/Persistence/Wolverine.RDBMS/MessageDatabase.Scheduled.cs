using Weasel.Core;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    public Task ScheduleExecutionAsync(Envelope envelope)
    {
        return CreateCommand(
                $"update {SchemaName}.{DatabaseConstants.IncomingTable} set execution_time = @time, status = \'{EnvelopeStatus.Scheduled}\', attempts = @attempts, owner_id = {TransportConstants.AnyNode} where id = @id;")
            .With("time", envelope.ScheduledTime!.Value)
            .With("attempts", envelope.Attempts)
            .With("id", envelope.Id)
            .ExecuteOnce(cancellation: _cancellation);
    }


    public Task ScheduleJobAsync(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;

        return StoreIncomingAsync(envelope);
    }
    

    public abstract void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow);
}