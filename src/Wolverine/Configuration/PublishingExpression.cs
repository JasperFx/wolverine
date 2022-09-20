using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Baseline;
using Wolverine.Util;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine.Configuration;

public class PublishingExpression : IPublishToExpression
{
    private readonly IList<Endpoint> _endpoints = new List<Endpoint>();
    private readonly IList<Subscription> _subscriptions = new List<Subscription>();

    internal PublishingExpression(WolverineOptions parent)
    {
        Parent = parent;
    }

    public WolverineOptions Parent { get; }

    internal bool AutoAddSubscriptions { get; set; }


    /// <summary>
    ///     All matching records are to be sent to the configured subscriber
    ///     by Uri
    /// </summary>
    /// <param name="uri"></param>
    /// <returns></returns>
    public ISubscriberConfiguration To(Uri uri)
    {
        var endpoint = Parent.Transports.GetOrCreateEndpoint(uri);

        _endpoints.Add(endpoint);

        if (AutoAddSubscriptions)
        {
            endpoint.Subscriptions.AddRange(_subscriptions);
        }

        return new SubscriberConfiguration(endpoint);
    }


    /// <summary>
    ///     Send all the matching messages to the designated Uri string
    /// </summary>
    /// <param name="uriString"></param>
    /// <returns></returns>
    public ISubscriberConfiguration To(string uriString)
    {
        return To(uriString.ToUri());
    }

    /// <summary>
    ///     Publishes the matching messages locally to the default
    ///     local queue
    /// </summary>
    public IListenerConfiguration Locally()
    {
        var settings = Parent.Transports.GetOrCreate<LocalTransport>().QueueFor(TransportConstants.Default);
        settings.Subscriptions.AddRange(_subscriptions);

        return new ListenerConfiguration(settings);
    }


    /// <summary>
    ///     Publish the designated message types to the named
    ///     local queue
    /// </summary>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public IListenerConfiguration ToLocalQueue(string queueName)
    {
        var settings = Parent.Transports.GetOrCreate<LocalTransport>().QueueFor(queueName);

        if (AutoAddSubscriptions)
        {
            settings.Subscriptions.AddRange(_subscriptions);
        }

        _endpoints.Add(settings);

        return new ListenerConfiguration(settings);
    }


    public PublishingExpression Message<T>()
    {
        return Message(typeof(T));
    }

    public PublishingExpression Message(Type type)
    {
        _subscriptions.Add(Subscription.ForType(type));
        return this;
    }

    public PublishingExpression MessagesFromNamespace(string @namespace)
    {
        _subscriptions.Add(new Subscription
        {
            Match = @namespace,
            Scope = RoutingScope.Namespace
        });

        return this;
    }

    public PublishingExpression MessagesFromNamespaceContaining<T>()
    {
        return MessagesFromNamespace(typeof(T).Namespace!);
    }

    public PublishingExpression MessagesFromAssembly(Assembly assembly)
    {
        _subscriptions.Add(new Subscription(assembly));
        return this;
    }

    public PublishingExpression MessagesFromAssemblyContaining<T>()
    {
        return MessagesFromAssembly(typeof(T).Assembly);
    }


    internal void AttachSubscriptions()
    {
        if (!_endpoints.Any())
        {
            throw new InvalidOperationException("No subscriber endpoint(s) are specified!");
        }

        foreach (var endpoint in _endpoints) endpoint.Subscriptions.AddRange(_subscriptions);
    }

    internal void AddSubscriptionForAllMessages()
    {
        _subscriptions.Add(Subscription.All());
    }
}
