using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Wolverine.RavenDb.Internals.Durability;

public partial class RavenDbDurabilityAgent
{
    private async Task runScheduledJobs()
    {
        try
        {
            if (!(await _parent.TryAttainScheduledJobLockAsync(_combined.Token)))
            {
                return;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to attain the scheduled job lock");
            return;
        }
        
        try
        {
            using var session = _store.OpenAsyncSession();
            var incoming = await session.Query<IncomingMessage>()
                .Where(x => x.Status == EnvelopeStatus.Scheduled && x.ExecutionTime <= DateTimeOffset.UtcNow)
                .OrderBy(x => x.ExecutionTime)
                .Take(_settings.RecoveryBatchSize)
                .ToListAsync(_combined.Token);

            if (!incoming.Any())
            {
                return;
            }
            
            await locallyPublishScheduledMessages(incoming, session);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to process ");
        }
        finally
        {
            try
            {
                await _parent.ReleaseScheduledJobLockAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to release the scheduled job lock");
            }
        }
    }

    /// <summary>
    /// GH-4710. Deliberately enqueues every promoted envelope locally rather than routing it by
    /// <c>Destination</c> through <c>runtime.EnqueueDirectlyAsync</c> the way every relational store does.
    /// That difference is real but, for the shapes that actually reach this method, it costs nothing --
    /// verified by running rather than by reading, in
    /// <c>RavenDbTests.Bug_4710_scheduled_promotion_routes_by_destination</c>.
    ///
    /// <para>The reason is that a delayed message addressed to an external endpoint does not park here under
    /// that destination. It parks as a <c>ScheduledEnvelope</c> WRAPPER addressed to this node's own
    /// <c>local://durable/</c>, so enqueuing it locally is exactly what has to happen:
    /// <c>ScheduledSendEnvelopeHandler</c> then unwraps it and sends it on. The row is settled as
    /// <c>Handled</c> afterwards, not stranded as <c>Incoming</c>. A DURABLE external destination takes a
    /// different path again -- it schedules in the outbox and never reaches this poller at all.</para>
    ///
    /// <para>So the three behaviours <c>EnqueueDirectlyAsync</c> carries have no RavenDb equivalent, and do
    /// not need one here: GH-4645's forward-and-retire (nothing arrives addressed elsewhere), GH-4700's
    /// slot-ownership hand-off (a sharded-queue global partition is not plausibly paired with RavenDb
    /// persistence), and GH-3413's dead-lettering of an unresolvable destination -- the one case left
    /// unverified, and reachable only by hand-writing a row with a transport this node cannot resolve.</para>
    ///
    /// <para>If that ever changes -- if some future path parks a scheduled row here under a genuinely remote
    /// address -- the first test in that fixture is what will say so, because it asserts the wrapper shape
    /// directly instead of trusting it.</para>
    /// </summary>
    private async Task locallyPublishScheduledMessages(List<IncomingMessage> incoming, IAsyncDocumentSession session)
    {
        var envelopes = incoming.Select(x => x.Read()).ToList();

        // GH-4216. Stamp the envelope's owning store on each promoted message so the rest of the
        // pipeline (DelegatingMessageInbox, DurableReceiver._markAsHandled) routes its writes back
        // to THIS store. Every relational store does this in its own poller; without it an
        // ancillary RavenDb store's rows are marked handled against the MAIN store, match nothing,
        // and are re-promoted on every pass. See GH-2576.
        foreach (var envelope in envelopes)
        {
            envelope.Store = _parent;
        }

        foreach (var message in incoming)
        {
            message.Status = EnvelopeStatus.Incoming;
            message.OwnerId = _settings.AssignedNodeNumber;
        }
            
        await session.SaveChangesAsync();

        // This is very low risk
        foreach (var envelope in envelopes)
        {
            _logger.LogInformation("Locally enqueuing scheduled message {Id} of type {MessageType}", envelope.Id,
                envelope.MessageType);
            await _localQueue.EnqueueAsync(envelope);
        }
    }
}