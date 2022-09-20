using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wolverine.Transports;
using Weasel.Core;

namespace Wolverine.RDBMS;

public abstract partial class DatabaseBackedEnvelopePersistence<T>
{
    public Task ScheduleExecutionAsync(Envelope[] envelopes)
    {
        var builder = DatabaseSettings.ToCommandBuilder();

        foreach (var envelope in envelopes)
        {
            var id = builder.AddParameter(envelope.Id);
            var time = builder.AddParameter(envelope.ScheduledTime.Value);
            var attempts = builder.AddParameter(envelope.Attempts);

            builder.Append(
                $"update {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} set execution_time = @{time.ParameterName}, status = \'{EnvelopeStatus.Scheduled}\', attempts = @{attempts.ParameterName}, owner_id = {TransportConstants.AnyNode} where id = @{id.ParameterName};");
        }

        return builder.Compile().ExecuteOnce(_cancellation);
    }


    public Task ScheduleJobAsync(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;

        return StoreIncomingAsync(envelope);
    }


    public abstract Task<IReadOnlyList<Envelope>> LoadScheduledToExecuteAsync(DateTimeOffset utcNow);
}
