using JasperFx.Core;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IMessageInbox
{
    public async Task ScheduleExecutionAsync(Envelope envelope)
    {
        using var session = _store.OpenAsyncSession();
        var incoming = await session.LoadAsync<IncomingMessage>(envelope.Id.ToString());
        incoming.ExecutionTime = envelope.ScheduledTime;
        incoming.Attempts = envelope.Attempts;
        incoming.Status = EnvelopeStatus.Scheduled;
        incoming.OwnerId = TransportConstants.AnyNode;
        await session.StoreAsync(incoming);
        await session.SaveChangesAsync();
    }

    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        using var session = _store.OpenAsyncSession();
        session.Delete(envelope.Id.ToString());
        var dlq = new DeadLetterMessage(envelope, exception);
        await session.StoreAsync(dlq);
        await session.SaveChangesAsync();
    }

    public async Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        using var session = _store.OpenAsyncSession();
        session.Advanced.Patch<IncomingMessage, int>(envelope.Id.ToString(), x => x.Attempts, envelope.Attempts);
        await session.SaveChangesAsync();
    }

    public async Task StoreIncomingAsync(Envelope envelope)
    {
        // TODO -- gotta be a way to do this w/ less chattiness
        using var session = _store.OpenAsyncSession();
        
        if (await session.Advanced.ExistsAsync(envelope.Id.ToString()))
        {
            throw new DuplicateIncomingEnvelopeException(envelope.Id);
        }

        var incoming = new IncomingMessage(envelope);
        await session.StoreAsync(incoming);
        await session.SaveChangesAsync();
    }

    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        using var session = _store.OpenAsyncSession();
        foreach (var envelope in envelopes)
        {
            var incoming = new IncomingMessage(envelope);
            await session.StoreAsync(incoming);
        }
        
        await session.SaveChangesAsync();
    }

    public Task ScheduleJobAsync(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;

        return StoreIncomingAsync(envelope);
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        using var session = _store.OpenAsyncSession();
        session.Advanced.Patch<IncomingMessage, EnvelopeStatus>(envelope.Id.ToString(), x => x.Status, EnvelopeStatus.Handled);
        await session.SaveChangesAsync();
    }

    public async Task ReleaseIncomingAsync(int ownerId)
    {
        using var session = _store.OpenAsyncSession();

        var query = new IndexQuery
        {
            Query = $@"
from IncomingMessages as m
where m.OwnerId = {ownerId}
update
{{
    m.OwnerId = 0
}}",
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 10.Seconds()
        };

        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(query));
        await op.WaitForCompletionAsync();
    }

    public async Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        using var session = _store.OpenAsyncSession();
        var command = $@"
from IncomingMessages as m
where m.OwnerId = {ownerId} and m.Destination = '{receivedAt}'
update
{{
    m.OwnerId = 0
}}";

        var query = new IndexQuery
        {
            Query = command,
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 5.Seconds()
        };

        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(query));
        await op.WaitForCompletionAsync();
    }
}