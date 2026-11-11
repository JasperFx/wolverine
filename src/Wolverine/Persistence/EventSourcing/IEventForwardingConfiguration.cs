using JasperFx.Events;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
/// Event forwarding from an event store integrated with Wolverine: Marten's <c>MartenIntegration</c>, Polecat's
/// <c>PolecatIntegration</c> and Fisher's <c>FisherIntegration</c> all implement it, so code that configures event
/// forwarding can be written once against this interface. GH-5514: named apart from the per-store
/// <c>IEventForwarding</c> (obsolete until 8.0) so the two never collide in a file that imports both namespaces.
/// </summary>
public interface IEventForwardingConfiguration
{
    /// <summary>
    /// Subscribe to an event, but with a transformation. The transformed message will be
    /// published to Wolverine with its normal routing rules
    /// </summary>
    /// <typeparam name="T"></typeparam>
    EventForwardingTransformation<T> SubscribeToEvent<T>() where T : notnull;
}

/// <summary>
/// Completes a <see cref="IEventForwardingConfiguration.SubscribeToEvent{T}" /> with the transformation of the forwarded event.
/// </summary>
public class EventForwardingTransformation<TSource> where TSource : notnull
{
    private readonly EventForwardingRouter _eventRouter;

    internal EventForwardingTransformation(EventForwardingRouter eventRouter)
    {
        _eventRouter = eventRouter;
    }

    public void TransformedTo<TDestination>(Func<IEvent<TSource>, TDestination> transformer)
    {
        _eventRouter.CompleteTransformation(transformer);
    }
}
