using JasperFx.Core;
using Raven.Client;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Exceptions;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IMessageInbox
{
    public async Task ScheduleExecutionAsync(Envelope envelope)
    {
        var query = $@"
            from IncomingMessages as m
            where id() = $id
            update {{
                this.ExecutionTime = $time;
                this.Status = $status;
                this.Attempts = $attempts;
                this.OwnerId = 0;
            }}";

        var operation = new PatchByQueryOperation(new IndexQuery
        {
            Query = query,
            WaitForNonStaleResults = true,
            QueryParameters = new Parameters()
            {
                {"id", envelope.Id.ToString()},
                {"attempts", envelope.Attempts},
                {"status", EnvelopeStatus.Scheduled},
                {"time", envelope.ScheduledTime}
            }
        });
        
        var op = await _store.Operations.SendAsync(operation);
        await op.WaitForCompletionAsync();
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
        using var session = _store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        
        var incoming = new IncomingMessage(envelope);

        try
        {
            await session.StoreAsync(incoming);
            await session.SaveChangesAsync();
        }
        catch (ConcurrencyException)
        {
            throw new DuplicateIncomingEnvelopeException(envelope.Id);
        }
    }

    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        using var session = _store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        
        foreach (var envelope in envelopes)
        {
            var incoming = new IncomingMessage(envelope);
            await session.StoreAsync(incoming);
        }

        // It's okay if it does fail here with the duplicate detection, because that
        // will force the DurableReceiver to try envelope at a time to get at the actual differences
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
        var expirationTime = DateTimeOffset.UtcNow.Add(_runtime.Options.Durability.KeepAfterMessageHandling);
        
        var query = $@"
            from IncomingMessages as m
            where id() = $id
            update {{
                this[""@metadata""][""@expires""] = $expire;
                this.Status = $status;
                
            }}";


        var operation = new PatchByQueryOperation(new IndexQuery
        {
            Query = query,
            WaitForNonStaleResults = true,
            QueryParameters = new Parameters()
            {
                {"id", envelope.Id.ToString()},
                {"expire", expirationTime},
                {"status", EnvelopeStatus.Handled}
            }
        });
        
        var op = await _store.Operations.SendAsync(operation);
        await op.WaitForCompletionAsync();
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        var expirationTime = DateTimeOffset.UtcNow.Add(_runtime.Options.Durability.KeepAfterMessageHandling);
        
        var query = $@"
            from IncomingMessages as m
            where id() in ($ids)
            update {{
                this[""@metadata""][""@expires""] = $expire;
                this.Status = $status;
                
            }}";


        var operation = new PatchByQueryOperation(new IndexQuery
        {
            Query = query,
            WaitForNonStaleResults = true,
            QueryParameters = new Parameters()
            {
                {"ids", envelopes.Select(x => x.Id.ToString()).ToArray()},
                {"expire", expirationTime},
                {"status", EnvelopeStatus.Handled}
            }
        });
        
        var op = await _store.Operations.SendAsync(operation);
        await op.WaitForCompletionAsync();
    }

    public async Task ReleaseIncomingAsync(int ownerId)
    {
        using var session = _store.OpenAsyncSession();

        var query = new IndexQuery
        {
            Query = $@"
from IncomingMessages as m
where m.OwnerId = $owner
update
{{
    m.OwnerId = 0
}}",
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 10.Seconds(),
            QueryParameters = new()
            {
                {"owner", ownerId}
            }
        };

        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(query));
        await op.WaitForCompletionAsync();
    }

    public async Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        using var session = _store.OpenAsyncSession();
        var command = $@"
from IncomingMessages as m
where m.OwnerId = $owner and m.Destination = $uri
update
{{
    m.OwnerId = 0
}}";

        var query = new IndexQuery
        {
            Query = command,
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 5.Seconds(),
            QueryParameters = new Parameters()
            {
                {"owner", ownerId},
                {"uri", receivedAt}
            }
        };

        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(query));
        await op.WaitForCompletionAsync();
    }
}