using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Routing;

/// <summary>
///     The non-generic router base. Carries the message type as a <see cref="Type" /> rather than as a type
///     parameter, so the runtime can construct a router for any message type without closing a generic.
/// </summary>
/// <remarks>
///     GH-4848. The generic <see cref="MessageRouterBase{T}" /> only ever used <c>T</c> for <c>typeof(T)</c>
///     and for typed overloads that <see cref="IMessageRouter" /> exposes as <c>object</c> anyway, while the
///     runtime closed it over every message type reflectively at startup. In a native image that close had
///     to be rooted per message type, could not be rooted at all for a value type (a handler returning
///     <c>Guid</c> died in <c>PrepopulateRoutingCache</c>), and needed a <c>typeof</c> token for an
///     interface. None of that applies to a class that takes the <see cref="Type" /> as an argument, so the
///     hazard is deleted rather than rooted. The generic classes remain for anything that referenced them,
///     but the runtime no longer constructs them.
/// </remarks>
public abstract class MessageRouterBase : IMessageRouter
{
    private readonly MessageRoute[] _topicRoutes;

    private ImHashMap<Uri, IMessageRoute> _specificRoutes = ImHashMap<Uri, IMessageRoute>.Empty;

    protected MessageRouterBase(WolverineRuntime runtime, Type messageType)
    {
        MessageType = messageType ?? throw new ArgumentNullException(nameof(messageType));

        // We'll use this for executing scheduled envelopes that aren't native
        LocalDurableQueue = runtime.Endpoints.GetOrBuildSendingAgent(TransportConstants.DurableLocalUri);

        var chain = runtime.Handlers.ChainFor(messageType);
        if (chain != null)
        {
            foreach (var handler in chain.Handlers)
            {
                foreach (var attribute in handler.Method.GetAllAttributes<ModifyEnvelopeAttribute>())
                {
                    if (attribute is IEnvelopeRule rule) HandlerRules.Add(rule);
                }
            }

            foreach (var byEndpoint in chain.ByEndpoint)
            {
                foreach (var handler in byEndpoint.Handlers)
                {
                    foreach (var attribute in handler.Method.GetAllAttributes<ModifyEnvelopeAttribute>())
                    {
                        if (attribute is IEnvelopeRule rule) HandlerRules.Add(rule);
                    }
                }
            }
        }

        foreach (var attribute in messageType.GetAllAttributes<ModifyEnvelopeAttribute>())
        {
            if (attribute is IEnvelopeRule rule) HandlerRules.Add(rule);
        }

        var topicRouteList = new List<MessageRoute>();
        foreach (var endpoint in runtime.Options.Transports.AllEndpoints())
        {
            if (endpoint.RoutingType == RoutingMode.ByTopic)
            {
                topicRouteList.Add(MessageRoute.For(messageType, endpoint, runtime));
            }
        }

        _topicRoutes = topicRouteList.ToArray();

        Runtime = runtime;
    }

    /// <summary>
    ///     The message type this router routes.
    /// </summary>
    public Type MessageType { get; }

    internal WolverineRuntime Runtime { get; }

    public ISendingAgent LocalDurableQueue { get; }

    public List<IEnvelopeRule> HandlerRules { get; } = new();
    public abstract IMessageRoute[] Routes { get; }

    public abstract Envelope[] RouteForSend(object message, DeliveryOptions? options);
    public abstract Envelope[] RouteForPublish(object message, DeliveryOptions? options);

    public abstract IMessageRoute FindSingleRouteForSending();

    public Envelope RouteToDestination(object message, Uri uri, DeliveryOptions? options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return RouteForUri(uri)
            .CreateForSending(message, options, LocalDurableQueue, Runtime, null);
    }

    public IMessageRoute RouteForUri(Uri destination)
    {
        if (_specificRoutes.TryFind(destination, out var route))
        {
            return route;
        }

        var agent = Runtime.Endpoints.GetOrBuildSendingAgent(destination);
        route = MessageRoute.For(MessageType, agent.Endpoint, Runtime);
        _specificRoutes = _specificRoutes.AddOrUpdate(destination, route);

        return route;
    }

    public Envelope[] RouteToTopic(object message, string topicName, DeliveryOptions? options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        if (_topicRoutes.Length == 0)
        {
            throw new InvalidOperationException("There are no registered topic routed endpoints");
        }

        var envelopes = new Envelope[_topicRoutes.Length];
        for (var i = 0; i < envelopes.Length; i++)
        {
            envelopes[i] = _topicRoutes[i].CreateForSending(message, options, LocalDurableQueue, Runtime, topicName);
        }

        return envelopes;
    }
}

/// <summary>
///     Retained for compatibility. The runtime builds the non-generic <see cref="MessageRouterBase" />
///     family since GH-4848 and never constructs this one; see the remarks there.
/// </summary>
public abstract class MessageRouterBase<T> : MessageRouterBase
{
    protected MessageRouterBase(WolverineRuntime runtime) : base(runtime, typeof(T))
    {
    }

    public sealed override Envelope[] RouteForSend(object message, DeliveryOptions? options)
    {
        return RouteForSend((T)message, options);
    }

    public sealed override Envelope[] RouteForPublish(object message, DeliveryOptions? options)
    {
        return RouteForPublish((T)message, options);
    }

    public abstract Envelope[] RouteForSend(T message, DeliveryOptions? options);
    public abstract Envelope[] RouteForPublish(T message, DeliveryOptions? options);

    public Envelope RouteToDestination(T message, Uri uri, DeliveryOptions? options)
    {
        return RouteToDestination((object)message!, uri, options);
    }

    public Envelope[] RouteToTopic(T message, string topicName, DeliveryOptions? options)
    {
        return RouteToTopic((object)message!, topicName, options);
    }
}
