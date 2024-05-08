using System.Reflection;
using JasperFx.Core;
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

        AddSubscriber(endpoint);

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
    public LocalQueueConfiguration Locally()
    {
        var settings = Parent.Transports.GetOrCreate<LocalTransport>().QueueFor(TransportConstants.Default);
        settings.Subscriptions.AddRange(_subscriptions);

        return new LocalQueueConfiguration(settings);
    }

    /// <summary>
    ///     Publish the designated message types to the named
    ///     local queue
    /// </summary>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public LocalQueueConfiguration ToLocalQueue(string queueName)
    {
        var settings = Parent.Transports.GetOrCreate<LocalTransport>().QueueFor(queueName);

        if (AutoAddSubscriptions)
        {
            settings.Subscriptions.AddRange(_subscriptions);
        }

        _endpoints.Add(settings);

        return new LocalQueueConfiguration(settings);
    }

    public void AddSubscriber(Endpoint endpoint)
    {
        _endpoints.Add(endpoint);

        if (AutoAddSubscriptions)
        {
            endpoint.Subscriptions.AddRange(_subscriptions);
        }
    }

    /// <summary>
    ///     Create a publishing rule for a single message type
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public PublishingExpression Message<T>()
    {
        return Message(typeof(T));
    }

    /// <summary>
    ///     Create a publishing rule for a single message type
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public PublishingExpression Message(Type type)
    {
        _subscriptions.Add(Subscription.ForType(type));
        return this;
    }

    /// <summary>
    ///     Create a publishing rule for all message types from within the
    ///     specified namespace
    /// </summary>
    /// <param name="namespace"></param>
    /// <returns></returns>
    public PublishingExpression MessagesFromNamespace(string @namespace)
    {
        _subscriptions.Add(new Subscription
        {
            Match = @namespace,
            Scope = RoutingScope.Namespace
        });

        return this;
    }

    /// <summary>
    ///     Create a publishing rule for all message types from within the
    ///     specified namespace holding the marker type "T"
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public PublishingExpression MessagesFromNamespaceContaining<T>()
    {
        return MessagesFromNamespace(typeof(T).Namespace!);
    }

    /// <summary>
    ///     Create a publishing rule for all messages from the given assembly
    /// </summary>
    /// <param name="assembly"></param>
    /// <returns></returns>
    public PublishingExpression MessagesFromAssembly(Assembly assembly)
    {
        _subscriptions.Add(new Subscription(assembly));
        return this;
    }

    /// <summary>
    ///     Create a publishing rule for all messages from the given assembly that contains the type T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
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

    /// <summary>
    ///     Publish all messages implementing a marker interface or inheriting from a common
    ///     base class
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void MessagesImplementing<T>()
    {
        _subscriptions.Add(new Subscription { BaseType = typeof(T), Scope = RoutingScope.Implements });
    }
}