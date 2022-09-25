using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wolverine.Logging;
using Wolverine.Runtime.Scheduled;

namespace Wolverine.Persistence.Durability;

public class NullEnvelopePersistence : IEnvelopePersistence, IEnvelopeStorageAdmin
{
    public IScheduledJobProcessor? ScheduledJobs { get; set; }
    public IEnvelopeStorageAdmin Admin => this;

    public Task DeleteIncomingEnvelopesAsync(Envelope[] envelopes)
    {
        return Task.CompletedTask;
    }

    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        return Task.CompletedTask;
    }

    public Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        return Task.CompletedTask;
    }

    public Task DeleteOutgoingAsync(Envelope envelope)
    {
        return Task.CompletedTask;
    }

    public Task MoveToDeadLetterStorageAsync(ErrorReport[] errors)
    {
        return Task.CompletedTask;
    }

    public Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? ex)
    {
        return Task.CompletedTask;
    }

    public Task ScheduleExecutionAsync(Envelope[] envelopes)
    {
        return Task.CompletedTask;
    }

    public Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        return Task.CompletedTask;
    }

    public Task<ErrorReport?> LoadDeadLetterEnvelopeAsync(Guid id)
    {
        throw new NotSupportedException();
    }

    public Task DeleteExpiredHandledEnvelopesAsync(DateTimeOffset utcNow)
    {
        return Task.CompletedTask;
    }

    public Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        return Task.CompletedTask;
    }

    public Task StoreIncomingAsync(Envelope envelope)
    {
        if (envelope.Status == EnvelopeStatus.Scheduled)
        {
            if (envelope.ScheduledTime == null)
            {
                throw new ArgumentOutOfRangeException(
                    $"The envelope {envelope} is marked as Scheduled, but does not have an ExecutionTime");
            }

            ScheduledJobs?.Enqueue(envelope.ScheduledTime.Value, envelope);
        }

        return Task.CompletedTask;
    }

    public Task StoreIncomingAsync(Envelope[] envelopes)
    {
        foreach (var envelope in envelopes.Where(x => x.Status == EnvelopeStatus.Scheduled))
            ScheduledJobs?.Enqueue(envelope.ScheduledTime!.Value, envelope);

        return Task.CompletedTask;
    }

    public Task<Uri[]> FindAllDestinationsAsync()
    {
        throw new NotSupportedException();
    }

    public Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        return Task.CompletedTask;
    }

    public Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        return Task.CompletedTask;
    }

    public Task StoreOutgoingAsync(Envelope[] envelopes, int ownerId)
    {
        return Task.CompletedTask;
    }

    public void Describe(TextWriter writer)
    {
        writer.WriteLine("No persistent envelope storage");
    }

    public Task ScheduleJobAsync(Envelope envelope)
    {
        if (!envelope.ScheduledTime.HasValue)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope),
                $"Envelope does not have a value for {nameof(Envelope.ScheduledTime)}");
        }

        ScheduledJobs?.Enqueue(envelope.ScheduledTime!.Value, envelope);

        return Task.CompletedTask;
    }


    public void Dispose()
    {
        // Nothing
    }

    public IDurableStorageSession Session => throw new NotSupportedException();

    public Task<IReadOnlyList<Envelope>> LoadScheduledToExecuteAsync(DateTimeOffset utcNow)
    {
        throw new NotSupportedException();
    }

    public Task ReassignDormantNodeToAnyNodeAsync(int nodeId)
    {
        throw new NotSupportedException();
    }

    public Task<int[]> FindUniqueOwnersAsync(int currentNodeId)
    {
        throw new NotSupportedException();
    }

    public Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        throw new NotSupportedException();
    }

    public Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing)
    {
        throw new NotSupportedException();
    }

    public Task DeleteByDestinationAsync(Uri? destination)
    {
        throw new NotSupportedException();
    }

    public Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync()
    {
        throw new NotSupportedException();
    }

    public Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        throw new NotSupportedException();
    }

    public Task ReleaseIncomingAsync(int ownerId)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Envelope>> AllIncomingAsync()
    {
        return Task.FromResult((IReadOnlyList<Envelope>)new List<Envelope>());
    }

    public Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        return Task.FromResult((IReadOnlyList<Envelope>)new List<Envelope>());
    }

    public Task ReleaseAllOwnershipAsync()
    {
        return Task.CompletedTask;
    }

    public Task CheckAsync(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task<PersistedCounts> FetchCountsAsync()
    {
        // Nothing to do, but keeps the metrics from blowing up
        return Task.FromResult(new PersistedCounts());
    }

    public Task ClearAllAsync()
    {
        return Task.CompletedTask;
    }

    public Task RebuildAsync()
    {
        return Task.CompletedTask;
    }
}
