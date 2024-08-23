using Raven.Client.Documents.Operations;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IMessageInbox
{
    public async Task ScheduleExecutionAsync(Envelope envelope)
    {
        throw new NotImplementedException();
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
        session.Advanced.Increment<IncomingMessage, int>(envelope.Id.ToString(), x => x.Attempts, 1);
        await session.SaveChangesAsync();
    }

    public async Task StoreIncomingAsync(Envelope envelope)
    {
        var incoming = new IncomingMessage(envelope);
        using var session = _store.OpenAsyncSession();
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

    public async Task ScheduleJobAsync(Envelope envelope)
    {
        throw new NotImplementedException();
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
        var command = $@"
from IncomingMessages as m
where m.OwnerId = {ownerId}
update
{{
    m.OwnerId = 0
}}";

        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(command));
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

        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(command));
        await op.WaitForCompletionAsync();
    }
}