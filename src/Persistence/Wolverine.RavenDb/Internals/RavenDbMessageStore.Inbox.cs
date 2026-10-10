using JasperFx.Core;
using Raven.Client;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents.Session;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IMessageInbox
{
    public async Task ScheduleExecutionAsync(Envelope envelope)
    {
        // GH-4216: a Handled document is never resurrected by a late retry booking. It is the dedup window's
        // record that the message completed; flipping it back to Scheduled with its body intact ran the
        // message a second time. The check lives in the patch script rather than the query on purpose: a
        // query on id() alone needs no index, while "and m.Status != $handled" made this an index query that
        // CI's RavenDB refused with "Cannot perform bulk operation. Index is stale."
        var query = $@"
            from IncomingMessages as m
            where id() = $id
            update {{
                if (this.Status !== $handled) {{
                    this.ExecutionTime = $time;
                    this.Status = $status;
                    this.Attempts = $attempts;
                    this.OwnerId = 0;
                }}
            }}";

        var operation = new PatchByQueryOperation(new IndexQuery
        {
            Query = query,
            WaitForNonStaleResults = true,
            QueryParameters = new Parameters()
            {
                {"id", _identity(envelope)},
                {"attempts", envelope.Attempts},
                {"status", EnvelopeStatus.Scheduled},
                {"handled", EnvelopeStatus.Handled},
                {"time", envelope.ScheduledTime}
            }
        });
        
        var op = await _store.Operations.SendAsync(operation);
        await op.WaitForCompletionAsync();
    }

    public async Task DeleteIncomingEnvelopeAsync(Envelope envelope)
    {
        using var session = _store.OpenAsyncSession();
        session.Delete(_identity(envelope));
        await session.SaveChangesAsync();
    }

    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        using var session = _store.OpenAsyncSession();
        session.Delete(_identity(envelope));
        var dlq = new DeadLetterMessage(envelope, exception);

        if (envelope.DeliverBy.HasValue)
        {
            dlq.ExpirationTime = envelope.DeliverBy.Value;
        }
        else
        {
            dlq.ExpirationTime = DateTimeOffset.UtcNow.Add(_options.Durability.DeadLetterQueueExpiration);
        }

        await session.StoreAsync(dlq);
        await session.SaveChangesAsync();
    }

    public async Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        using var session = _store.OpenAsyncSession();
        session.Advanced.Patch<IncomingMessage, int>(_identity(envelope), x => x.Attempts, envelope.Attempts);
        await session.SaveChangesAsync();
    }

    public async Task StoreIncomingAsync(Envelope envelope)
    {
        using var session = _store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        
        var incoming = new IncomingMessage(envelope, this);

        try
        {
            await session.StoreAsync(incoming);
            await session.SaveChangesAsync();
        }
        catch (ConcurrencyException)
        {
            throw new DuplicateIncomingEnvelopeException(envelope);
        }
    }

    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        using var session = _store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;

        try
        {
            foreach (var envelope in envelopes)
            {
                var incoming = new IncomingMessage(envelope, this);
                await session.StoreAsync(incoming);
            }

            await session.SaveChangesAsync();
        }
        catch (NonUniqueObjectException)
        {
            // Same envelope identity appeared twice in this batch (e.g. broker
            // redelivery race). Identify which envelopes already exist so
            // DurableReceiver only completes the actual duplicates and
            // re-pipelines the fresh ones.
            throw new DuplicateIncomingEnvelopeException(await findDuplicatesAsync(envelopes));
        }
        catch (ConcurrencyException)
        {
            // At least one envelope is already in the inbox; same fallback contract.
            throw new DuplicateIncomingEnvelopeException(await findDuplicatesAsync(envelopes));
        }
    }

    private async Task<IReadOnlyList<Envelope>> findDuplicatesAsync(IReadOnlyList<Envelope> envelopes)
    {
        var duplicates = new List<Envelope>();
        foreach (var envelope in envelopes)
        {
            if (await ExistsAsync(envelope, CancellationToken.None).ConfigureAwait(false))
            {
                duplicates.Add(envelope);
            }
        }

        // Backend reported a duplicate but no envelope id matches an existing
        // row (e.g. intra-batch collision with no prior insert). Surface every
        // envelope so the per-envelope retry path can sort it out.
        return duplicates.Count > 0 ? duplicates : envelopes;
    }

    public async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        using var session = _store.OpenAsyncSession();
        var identity = IdentityFor(envelope);
        return (await session.LoadAsync<IncomingMessage>(identity, cancellation)) != null;
    }

    public async Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;

        // GH-4216. This was INSERT-only, so it threw DuplicateIncomingEnvelopeException whenever a document already
        // existed for the identity -- which is the normal case this method exists for (GH-2462 / GH-2823 on the
        // relational stores). Update first, insert only when nothing is there, and leave a Handled document
        // alone: the message already completed, so a retry booked after the fact is discarded
        using var session = _store.OpenAsyncSession();
        var existing = await session.LoadAsync<IncomingMessage>(_identity(envelope));

        if (existing == null)
        {
            await StoreIncomingAsync(envelope);
            return;
        }

        if (existing.Status == EnvelopeStatus.Handled)
        {
            return;
        }

        await ScheduleExecutionAsync(envelope);
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        var expirationTime = DateTimeOffset.UtcNow.Add(_options.Durability.KeepAfterMessageHandling);
        
        // GH-4785: the owner goes with the status. A Handled document is kept only for idempotency and
        // nothing recovers it, so it has no owner -- the same as a document inserted already Handled
        // (Envelope.ForPersistedHandled). Safe because the agent's listener discovery now filters on
        // Incoming as well; see RavenDbDurabilityAgent.findListenersWithRecoverableIncomingAsync.
        var query = $@"
            from IncomingMessages as m
            where id() = $id
            update {{
                this[""@metadata""][""@expires""] = $expire;
                this.Status = $status;
                this.OwnerId = $owner;
            }}";


        var operation = new PatchByQueryOperation(new IndexQuery
        {
            Query = query,
            WaitForNonStaleResults = true,
            QueryParameters = new Parameters()
            {
                {"id", _identity(envelope)},
                {"expire", expirationTime},
                {"status", EnvelopeStatus.Handled},
                {"owner", TransportConstants.AnyNode}
            }
        });
        
        var op = await _store.Operations.SendAsync(operation);
        await op.WaitForCompletionAsync();
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        var expirationTime = DateTimeOffset.UtcNow.Add(_options.Durability.KeepAfterMessageHandling);
        
        // GH-4785: see the single-envelope overload above -- the batched patch has to release the owner too.
        var query = $@"
            from IncomingMessages as m
            where id() in ($ids)
            update {{
                this[""@metadata""][""@expires""] = $expire;
                this.Status = $status;
                this.OwnerId = $owner;
            }}";


        var identities = envelopes.Select(x => _identity(x)).ToArray();
        var operation = new PatchByQueryOperation(new IndexQuery
        {
            Query = query,
            WaitForNonStaleResults = true,
            QueryParameters = new Parameters()
            {
                {"ids", identities},
                {"expire", expirationTime},
                {"status", EnvelopeStatus.Handled},
                {"owner", TransportConstants.AnyNode}
            }
        });
        
        var op = await _store.Operations.SendAsync(operation);
        await op.WaitForCompletionAsync();
    }

    public async Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        using var session = _store.OpenAsyncSession();
        var command = $@"
from IncomingMessages as m
where m.OwnerId = $owner and m.ReceivedAt = $uri
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
                // GH-4216: the document's property is ReceivedAt (there is no Destination), stored as the Uri's string
                {"uri", receivedAt.ToString()}
            }
        };

        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(query));
        await op.WaitForCompletionAsync();
    }
}