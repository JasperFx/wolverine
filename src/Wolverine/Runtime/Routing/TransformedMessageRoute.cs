using JasperFx.Core.Reflection;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Routing;

internal class TransformedMessageRoute<TSource, TDestination> : IMessageRoute
{
    private readonly Func<TSource, TDestination> _transformation;
    private readonly IMessageRoute _inner;

    public TransformedMessageRoute(Func<TSource, TDestination> transformation, IMessageRoute inner)
    {
        _transformation = transformation;
        _inner = inner;
    }

    public MessageSubscriptionDescriptor Describe()
    {
        var descriptor = _inner.Describe();
        descriptor.Description = "Transformed to " + typeof(TDestination).FullNameInCode();
        return descriptor;
    }

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        var transformed = _transformation((TSource)message);
        return _inner.CreateForSending(transformed!, options, localDurableQueue, runtime, topicName);
    }

    public override string ToString()
    {
        return "Forward message as " + typeof(TDestination).FullNameInCode();
    }
}