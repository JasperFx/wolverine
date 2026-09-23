using System.Diagnostics;
using JasperFx.Events;
using Wolverine.Runtime;

namespace Wolverine.Marten.Publishing;

/// <summary>
///     The context an <see cref="ISendMyself"/> side effect is applied against, so that whatever it sends —
///     through PublishAsync(), EndpointFor() or a topic — carries the side effect's <see cref="MessageMetadata"/>.
///     Anything the message sets on its own delivery options wins.
/// </summary>
internal class ProjectionSideEffectContext : MessageContext
{
    private readonly MessageMetadata _metadata;

    public ProjectionSideEffectContext(IWolverineRuntime runtime, MessageMetadata metadata)
        : base(runtime, metadata.TenantId)
    {
        _metadata = metadata;
        CorrelationId = metadata.CorrelationId;
        MultiFlushMode = MultiFlushMode.AllowMultiples;
    }

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
