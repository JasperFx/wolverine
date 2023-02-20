using System;
using System.Collections.Generic;
using System.Linq;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Routing;

public abstract class MessageRouterBase<T> : IMessageRouter
{
    private readonly MessageRoute[] _topicRoutes;
    
    private ImHashMap<string, IMessageRoute> _routeByName = ImHashMap<string, IMessageRoute>.Empty;

    private ImHashMap<Uri, IMessageRoute> _specificRoutes = ImHashMap<Uri, IMessageRoute>.Empty;

    protected MessageRouterBase(WolverineRuntime runtime)
    {
        // We'll use this for executing scheduled envelopes that aren't native
        LocalDurableQueue = runtime.Endpoints.GetOrBuildSendingAgent(TransportConstants.DurableLocalUri);

        var chain = runtime.Handlers.ChainFor(typeof(T));
        if (chain != null)
        {
            var handlerRules = chain.Handlers.SelectMany(x => x.Method.GetAllAttributes<ModifyEnvelopeAttribute>())
                .OfType<IEnvelopeRule>();
            HandlerRules.AddRange(handlerRules);
        }

        _topicRoutes = runtime.Options.Transports.AllEndpoints().Where(x => x.RoutingType == RoutingMode.ByTopic)
            .Select(endpoint => new MessageRoute(typeof(T), endpoint, runtime.Replies)).ToArray();

        Runtime = runtime;
    }

    internal WolverineRuntime Runtime { get; }

    public ISendingAgent LocalDurableQueue { get; }

    public List<IEnvelopeRule> HandlerRules { get; } = new();
    public abstract MessageRoute[] Routes { get; }

    public Envelope[] RouteForSend(object message, DeliveryOptions? options)
    {
        return RouteForSend((T)message, options);
    }

    public Envelope[] RouteForPublish(object message, DeliveryOptions? options)
    {
        return RouteForPublish((T)message, options);
    }

    public Envelope RouteToDestination(object message, Uri uri, DeliveryOptions? options)
    {
        return RouteToDestination((T)message, uri, options);
    }

    public Envelope[] RouteToTopic(object message, string topicName, DeliveryOptions? options)
    {
        return RouteToTopic((T)message, topicName, options);
    }

    public abstract Envelope[] RouteForSend(T message, DeliveryOptions? options);
    public abstract Envelope[] RouteForPublish(T message, DeliveryOptions? options);

    public Envelope RouteToDestination(T message, Uri uri, DeliveryOptions? options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return RouteForUri(uri)
            .CreateForSending(message, options, LocalDurableQueue, Runtime);
    }

    public IMessageRoute RouteForUri(Uri destination)
    {
        if (_specificRoutes.TryFind(destination, out var route))
        {
            return route;
        }

        var agent = Runtime.Endpoints.GetOrBuildSendingAgent(destination);
        route = new MessageRoute(typeof(T), agent.Endpoint, Runtime.Replies);
        _specificRoutes = _specificRoutes.AddOrUpdate(destination, route);

        return route;
    }

    public abstract IMessageRoute FindSingleRouteForSending();

    public Envelope[] RouteToTopic(T message, string topicName, DeliveryOptions? options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        if (!_topicRoutes.Any())
        {
            throw new InvalidOperationException("There are no registered topic routed endpoints");
        }

        var envelopes = new Envelope[_topicRoutes.Length];
        for (var i = 0; i < envelopes.Length; i++)
        {
            envelopes[i] = _topicRoutes[i].CreateForSending(message, options, LocalDurableQueue, Runtime);
            envelopes[i].TopicName = topicName;
        }

        return envelopes;
    }

}