using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Weasel.Core;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

public abstract partial class MessageMessageDatabase<T>
{
    public Task ScheduleExecutionAsync(Envelope[] envelopes)
    {
        var builder = Settings.ToCommandBuilder();

        foreach (var envelope in envelopes)
        {
            var id = builder.AddParameter(envelope.Id);
            var time = builder.AddParameter(envelope.ScheduledTime!.Value);
            var attempts = builder.AddParameter(envelope.Attempts);

            builder.Append(
                $"update {Settings.SchemaName}.{DatabaseConstants.IncomingTable} set execution_time = @{time.ParameterName}, status = \'{EnvelopeStatus.Scheduled}\', attempts = @{attempts.ParameterName}, owner_id = {TransportConstants.AnyNode} where id = @{id.ParameterName};");
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