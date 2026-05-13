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
            return pooled;
        }

        fromPool = false;
        return new Envelope();
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
