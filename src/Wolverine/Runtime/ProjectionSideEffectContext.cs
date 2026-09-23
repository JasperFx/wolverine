using System.Diagnostics;
using JasperFx.Events;

namespace Wolverine.Runtime;

/// <summary>
///     The context an <see cref="ISendMyself" /> published from a projection side effect is applied
///     against, so that whatever it sends -- through PublishAsync(), EndpointFor() or a topic --
///     carries the side effect's <see cref="MessageMetadata" />. Anything the message set on its own
///     delivery options wins.
/// </summary>
/// <remarks>
///     A context per message rather than mutating TenantId / CorrelationId on the shared batch
///     context for the duration of ApplyAsync(): projection slices publish concurrently (the
///     aggregation runner's block parallelism is 10), so the shared context has no single correct
///     tenant while a page is being built.
/// </remarks>
internal class ProjectionSideEffectContext : MessageContext
{
    private readonly MessageMetadata _metadata;

    public ProjectionSideEffectContext(IWolverineRuntime runtime, MessageMetadata metadata,
        string? fallbackCorrelationId)
        : base(runtime, metadata.TenantId)
    {
        _metadata = metadata;

        // Only take over the correlation id when the projection actually supplied one.
        // MessageMetadata.CorrelationId is plain null otherwise (CorrelationIdEnabled is
        // literally "is not null"), and stamping that would strip the correlation id every
        // other side effect message out of this batch carries -- TrackEnvelopeCorrelation
        // copies this value onto each outgoing envelope. Falling back to the batch context's
        // id keeps the whole page on one correlation id. GH-4556.
        CorrelationId = _metadata.CorrelationIdEnabled ? _metadata.CorrelationId : fallbackCorrelationId;

        MultiFlushMode = MultiFlushMode.AllowMultiples;
    }

    /// <summary>
    ///     The single point every outgoing envelope passes through -- publish, EndpointFor(),
    ///     topics -- which is why the metadata is stamped here rather than mapped onto one
    ///     DeliveryOptions that only the outermost send would have seen.
    /// </summary>
    internal override void TrackEnvelopeCorrelation(Envelope outbound, Activity? activity)
    {
        if (_metadata.CausationIdEnabled)
        {
            outbound.Headers.TryAdd(EnvelopeConstants.CausationIdKey, _metadata.CausationId);
        }

        if (_metadata.HeadersEnabled)
        {
            foreach (var header in _metadata.Headers!)
            {
                outbound.Headers.TryAdd(header.Key, header.Value?.ToString());
            }
        }

        base.TrackEnvelopeCorrelation(outbound, activity);
    }
}
