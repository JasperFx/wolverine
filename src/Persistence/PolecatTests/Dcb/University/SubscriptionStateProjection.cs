using Polecat.Projections;

namespace PolecatTests.Dcb.University;

/// <summary>
/// Empty single-stream projection over <see cref="SubscriptionState"/>. Its only purpose is to give
/// the JasperFx.Events source generator a partial SingleStreamProjection&lt;,&gt; subclass to emit an
/// aggregator dispatcher for, which FetchForWritingByTags&lt;SubscriptionState&gt; then resolves. The
/// apply logic lives on SubscriptionState's conventional Apply methods. Registered with
/// ProjectionLifecycle.Live so nothing is persisted — this is Polecat's equivalent of the Marten DCB
/// test's LiveStreamAggregation&lt;SubscriptionState&gt;().
/// </summary>
public partial class SubscriptionStateProjection : SingleStreamProjection<SubscriptionState, string>
{
}
