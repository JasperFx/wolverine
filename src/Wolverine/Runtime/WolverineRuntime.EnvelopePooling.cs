using Wolverine.Transports.Sending;

namespace Wolverine.Runtime;

public sealed partial class WolverineRuntime
{
    /// <summary>
    /// Acquire an <see cref="Envelope"/> for use inside the framework's
    /// receive pipeline. Returns a pooled instance when no tracking session
    /// is active; allocates fresh when one is — that gate exists because
    /// <see cref="EnvelopeRecord"/> captures strong references to live
    /// envelopes for <see cref="ITrackedSession.Events"/> readers, and a
    /// recycled envelope would corrupt those readers' view of message
    /// history. See wolverine#2726, hazard Q1.
    /// </summary>
    /// <param name="fromPool">
    /// <c>true</c> when the returned envelope came from the pool and must
    /// be released via <see cref="ReleaseInternalEnvelope"/>; <c>false</c>
    /// when it was freshly allocated and can be discarded by the GC normally.
    /// </param>
    /// <returns>
    /// A blank-slate envelope. The caller is responsible for stamping
    /// <see cref="Envelope.Id"/> and <see cref="Envelope.SentAt"/> before
    /// use — <see cref="Envelope.Reset"/> deliberately zeroes both so the
    /// pool's blank-slate contract is invariant.
    /// </returns>
    internal Envelope AcquireInternalEnvelope(out bool fromPool)
    {
        if (ActiveSession is null)
        {
            fromPool = true;
            var pooled = EnvelopePool.Get();
            // Re-stamp the framing fields that Envelope's default constructor
            // would normally set. Reset() zeroes them on the way back to the
            // pool so the only place these defaults get applied is here, in
            // one consistent location.
            pooled.Id = Envelope.IdGenerator();
            pooled.SentAt = DateTimeOffset.UtcNow;
            pooled.AcceptedContentTypes = Envelope.DefaultAcceptedContentTypes;
            pooled.FromPool = true;
            return pooled;
        }

        fromPool = false;
        return new Envelope();
    }

    /// <summary>
    /// Outgoing-side pool acquire for <see cref="Routing.MessageRoute.CreateForSending"/>.
    /// Returns a pooled envelope only when the route's <paramref name="agent"/> has a
    /// bounded "fire and forget then release" lifecycle — concretely, when it is an
    /// <see cref="InlineSendingAgent"/>. Other agent shapes hold the envelope past the
    /// send call (buffered queues, durable outbox, local-queue → handler-dispatch) and
    /// can't be safely pooled with the current plumbing. See wolverine#2955.
    /// <para>
    /// The pool gate also requires <see cref="ActiveSession"/> to be null, same as
    /// <see cref="AcquireInternalEnvelope(out bool)"/> — tracking sinks would capture
    /// the envelope reference and a recycle would corrupt their history.
    /// </para>
    /// </summary>
    /// <returns>
    /// A blank-slate envelope with <see cref="Envelope.FromPool"/> stamped so the
    /// inline-send success path knows whether to release it. The caller still owns
    /// stamping <c>Message</c>, <c>Sender</c>, <c>Destination</c>, etc. — same
    /// contract as <see cref="AcquireInternalEnvelope(out bool)"/>.
    /// </returns>
    internal Envelope AcquireOutgoingEnvelope(ISendingAgent agent)
    {
        if (ActiveSession is null && IsPoolableOutgoingAgent(agent))
        {
            var pooled = EnvelopePool.Get();
            pooled.Id = Envelope.IdGenerator();
            pooled.SentAt = DateTimeOffset.UtcNow;
            pooled.AcceptedContentTypes = Envelope.DefaultAcceptedContentTypes;
            pooled.FromPool = true;
            return pooled;
        }

        return new Envelope();
    }

    // The senders whose lifetime ends at "successful send / mark-success" —
    // i.e. they release the envelope reference once SendAsync (or the agent's
    // success continuation) completes, leaving the envelope eligible for pool
    // recycle. Other agent shapes intentionally hold the envelope past that
    // point:
    //   - DurableSendingAgent persists into the outbox; the envelope's lifetime
    //     spans broker ack + outbox-row deletion, well beyond MarkSuccessful.
    //   - Local-queue agents (BufferedLocalQueue, DurableLocalQueue) forward
    //     envelopes to user handler code, where the envelope reference can
    //     be captured indefinitely.
    //
    // The ISenderRequiresCallback check excludes senders whose ack arrives via
    // a separate callback (rare in practice; per TenantedSender comments
    // RabbitMqSender etc. are simple fire-and-forget). The
    // sendWithCallbackHandlingAsync path doesn't release pooled envelopes so
    // those would otherwise leak past the pool's recycle window.
    private static bool IsPoolableOutgoingAgent(ISendingAgent agent)
    {
        return agent switch
        {
            InlineSendingAgent inline => inline.Sender is not ISenderRequiresCallback,
            BufferedSendingAgent buffered => buffered.Sender is not ISenderRequiresCallback,
            _ => false
        };
    }

    /// <summary>
    /// Release an envelope previously acquired via
    /// <see cref="AcquireInternalEnvelope"/>. No-op when <paramref name="fromPool"/>
    /// is <c>false</c>; the GC handles fresh allocations normally.
    /// </summary>
    internal void ReleaseInternalEnvelope(Envelope envelope, bool fromPool)
    {
        if (!fromPool) return;
        // EnvelopePoolPolicy.Return calls Envelope.Reset() before re-pooling.
        EnvelopePool.Return(envelope);
    }
}
