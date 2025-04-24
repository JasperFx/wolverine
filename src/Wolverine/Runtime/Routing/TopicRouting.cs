using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.Runtime.Routing;

public class TopicRouting<T> : IMessageRouteSource, IMessageRoute, IMessageInvoker
{
    private readonly Func<T, string> _topicSource;
    private readonly Endpoint _topicEndpoint;
    private MessageRoute? _route;

    public TopicRouting(Func<T, string> topicSource, Endpoint topicEndpoint)
    {
        _topicSource = topicSource;
        _topicEndpoint = topicEndpoint;
    }

    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        if (messageType.CanBeCastTo<T>())
        {
            yield return this;
        }
    }

    public bool IsAdditive => true;

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        if (message is T typedMessage)
        {
            _route ??= _topicEndpoint.RouteFor(typeof(T), runtime);
            topicName ??= _topicSource(typedMessage);
            var envelope = _route.CreateForSending(message, options, localDurableQueue, runtime, topicName);

            // This is an unfortunate timing of operation issue.
            if (envelope is { Message: Envelope scheduled, Status: EnvelopeStatus.Scheduled })
            {
                scheduled.TopicName = envelope.TopicName;
            }

            return envelope;
        }

        throw new InvalidOperationException(
            $"The message of type {message.GetType().FullNameInCode()} cannot be routed as a message of type {typeof(T).FullNameInCode()}");
    }

    public Task<T1> InvokeAsync<T1>(object message, MessageBus bus, CancellationToken cancellation = default, TimeSpan? timeout = null,
        string? tenantId = null)
    {
        if (message is T typedMessage)
        {
            _route ??= _topicEndpoint.RouteFor(typeof(T), bus.Runtime);
            var topicName = _topicSource(typedMessage);

            return _route.RemoteInvokeAsync<T1>(message, bus, cancellation, timeout, tenantId, topicName);
        }

        throw new InvalidOperationException(
            $"The message of type {message.GetType().FullNameInCode()} cannot be routed as a message of type {typeof(T).FullNameInCode()}");
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default, TimeSpan? timeout = null,
        string? tenantId = null)
    {
        return InvokeAsync<Acknowledgement>(message, bus, cancellation, timeout, tenantId);
    }

    public override string ToString()
    {
        return $"Topic routing to {_topicEndpoint.Uri}";
    }
}

internal static class TopicRouting
{
    private static ImHashMap<Type, string> _topics = ImHashMap<Type, string>.Empty;

    public static string DetermineTopicName(Type messageType)
    {
        if (_topics.TryFind(messageType, out var topic))
        {
            return topic;
        }

        topic = messageType.TryGetAttribute<TopicAttribute>(out var attribute)
            ? attribute.TopicName
            : messageType.ToMessageTypeName();

        _topics = _topics.AddOrUpdate(messageType, topic);

        return topic;
    }

    public static string DetermineTopicName(Envelope envelope)
    {
        if (envelope.TopicName.IsNotEmpty()) return envelope.TopicName;

        if (envelope.Message == null)
            throw new ArgumentNullException(nameof(envelope),
                $"{nameof(envelope.Message)} is null, making this operation invalid");

        return envelope.TopicName ?? DetermineTopicName(envelope.Message?.GetType());
    }
}

public class TopicRoutingRule : IEnvelopeRule
{
    public void Modify(Envelope envelope)
    {
        envelope.TopicName ??= TopicRouting.DetermineTopicName(envelope);
    }
}