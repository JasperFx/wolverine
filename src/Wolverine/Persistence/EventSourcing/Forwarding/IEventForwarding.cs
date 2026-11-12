using JasperFx.Events;

namespace Wolverine.Persistence.EventSourcing.Forwarding;

/// <summary>
/// Event forwarding from an event store integrated with Wolverine: Marten's <c>MartenIntegration</c>, Polecat's
/// <c>PolecatIntegration</c> and Fisher's <c>FisherIntegration</c> all implement it, so code that configures event
/// forwarding can be written once against this interface. GH-5514.
/// </summary>
public interface IEventForwarding
{
    /// <summary>
    /// Subscribe to an event, but with a transformation. The transformed message will be
    /// published to Wolverine with its normal routing rules
    /// </summary>
    /// <typeparam name="T"></typeparam>
    EventForwardingTransform<T> SubscribeToEvent<T>() where T : notnull;
}

/// <summary>
/// Completes a <see cref="IEventForwarding.SubscribeToEvent{T}" /> with the transformation of the forwarded event.
/// </summary>
public class EventForwardingTransform<TSource> where TSource : notnull
{
    private readonly EventForwardingRouter _eventRouter;

    internal EventForwardingTransform(EventForwardingRouter eventRouter)
    {
        _eventRouter = eventRouter;
    }

    public void TransformedTo<TDestination>(Func<IEvent<TSource>, TDestination> transformer)
    {
        _eventRouter.CompleteTransformation(transformer);
    }
}
