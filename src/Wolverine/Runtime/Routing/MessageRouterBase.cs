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
    private readonly MessageRoute _local;

    private readonly MessageRoute[] _topicRoutes;
    private ImHashMap<string, MessageRoute> _localRoutes = ImHashMap<string, MessageRoute>.Empty;
    private ImHashMap<string, IMessageRoute> _routeByName = ImHashMap<string, IMessageRoute>.Empty;

    private ImHashMap<Uri, MessageRoute> _specificRoutes = ImHashMap<Uri, MessageRoute>.Empty;

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

        _local = new MessageRoute(typeof(T), runtime.DetermineLocalSendingAgent(typeof(T)).Endpoint);
        _local.Rules.AddRange(HandlerRules!);

        _topicRoutes = runtime.Options.Transports.AllEndpoints().Where(x => x.RoutingType == RoutingMode.ByTopic)
            .Select(endpoint => new MessageRoute(typeof(T), endpoint)).ToArray();

        Runtime = runtime;
    }

    internal WolverineRuntime Runtime { get; }

    public ISendingAgent LocalDurableQueue { get; }

    public List<IEnvelopeRule> HandlerRules { get; } = new();

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

    public Envelope RouteToEndpointByName(object message, string endpointName, DeliveryOptions? options)
    {
        return RouteToEndpointByName((T)message, endpointName, options);
    }

    public Envelope[] RouteToTopic(object message, string topicName, DeliveryOptions? options)
    {
        return RouteToTopic((T)message, topicName, options);
    }

    public Envelope RouteLocal(object message, DeliveryOptions? options)
    {
        return RouteLocal((T)message, options);
    }

    public Envelope RouteLocal(object message, string workerQueue, DeliveryOptions? options)
    {
        return RouteLocal((T)message, workerQueue, options);
    }

    public abstract Envelope[] RouteForSend(T message, DeliveryOptions? options);
    public abstract Envelope[] RouteForPublish(T message, DeliveryOptions? options);

    public Envelope RouteToDestination(T message, Uri uri, DeliveryOptions? options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        if (_specificRoutes.TryFind(uri, out var route))
        {
            return route.CreateForSending(message, options, LocalDurableQueue, Runtime);
        }

        var agent = Runtime.Endpoints.GetOrBuildSendingAgent(uri);
        route = new MessageRoute(message.GetType(), agent.Endpoint);
        _specificRoutes = _specificRoutes.AddOrUpdate(uri, route);

        return route.CreateForSending(message, options, LocalDurableQueue, Runtime);
    }

    public Envelope RouteToEndpointByName(T message, string endpointName, DeliveryOptions? options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        if (_routeByName.TryFind(endpointName, out var route))
        {
            return route.CreateForSending(message, options, LocalDurableQueue, Runtime);
        }

        var endpoint = Runtime.Endpoints.EndpointByName(endpointName);
        route = endpoint == null
            ? new NoNamedEndpointRoute(endpointName,
                Runtime.Options.Transports.AllEndpoints().Select(x => x.EndpointName).ToArray())
            : new MessageRoute(typeof(T), Runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri).Endpoint);

        _routeByName = _routeByName.AddOrUpdate(endpointName, route);

        return route.CreateForSending(message, options, LocalDurableQueue, Runtime);
    }

    public Envelope RouteLocal(T message, DeliveryOptions? options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return _local.CreateForSending(message, options, LocalDurableQueue, Runtime);
    }

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

    public Envelope RouteLocal(T message, string workerQueue, DeliveryOptions? options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        workerQueue = workerQueue.ToLowerInvariant();
        if (_localRoutes.TryFind(workerQueue, out var route))
        {
            return route.CreateForSending(message, options, LocalDurableQueue, Runtime);
        }

        var queue = Runtime.Endpoints.AgentForLocalQueue(workerQueue);
        route = new MessageRoute(typeof(T), queue.Endpoint);
        route.Rules.AddRange(HandlerRules);
        _localRoutes = _localRoutes.AddOrUpdate(workerQueue, route);

        return route.CreateForSending(message, options, LocalDurableQueue, Runtime);
    }
}