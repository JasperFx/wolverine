using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine;

public partial class WolverineOptions : IAsyncDisposable
{
    internal IList<IMessageRoutingConvention> RoutingConventions { get; } = new List<IMessageRoutingConvention>();

    public IListenerConfiguration DefaultLocalQueue => LocalQueue(TransportConstants.Default);
    public IListenerConfiguration DurableScheduledMessagesLocalQueue => LocalQueue(TransportConstants.Durable);


    public ValueTask DisposeAsync()
    {
        return Transports.DisposeAsync();
    }

    /// <summary>
    ///     Register a routing convention that Wolverine will use to discover and apply
    ///     message routing
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public T RouteWith<T>() where T : IMessageRoutingConvention, new()
    {
        var convention = new T();
        RouteWith(convention);

        return convention;
    }

    /// <summary>
    ///     Register a routing convention that Wolverine will use to discover and apply
    ///     message routing
    /// </summary>
    /// <param name="routingConvention"></param>
    public void RouteWith(IMessageRoutingConvention routingConvention)
    {
        RoutingConventions.Add(routingConvention);
    }

    /// <summary>
    ///     Directs Wolverine to set up an incoming listener for the given Uri
    /// </summary>
    /// <param name="uri"></param>
    public IListenerConfiguration ListenForMessagesFrom(Uri uri)
    {
        var endpoint = Transports.Find(uri).GetOrCreateEndpoint(uri);
        endpoint.IsListener = true;
        return new ListenerConfiguration(endpoint);
    }

    /// <summary>
    ///     Directs Wolverine to set up an incoming listener for the given Uri
    /// </summary>
    public IListenerConfiguration ListenForMessagesFrom(string uriString)
    {
        return ListenForMessagesFrom(new Uri(uriString));
    }


    public void Publish(Action<PublishingExpression> configuration)
    {
        var expression = new PublishingExpression(this);
        configuration(expression);
        expression.AttachSubscriptions();
    }

    /// <summary>
    ///     Create a sending endpoint with no subscriptions. This
    ///     can be useful for programmatic sending to named endpoints
    /// </summary>
    /// <returns></returns>
    public PublishingExpression Publish()
    {
        return new PublishingExpression(this);
    }

    /// <summary>
    ///     Shorthand syntax to route a single message type
    /// </summary>
    /// <typeparam name="TMessageType"></typeparam>
    /// <returns></returns>
    public PublishingExpression PublishMessage<TMessageType>()
    {
        var expression = new PublishingExpression(this)
        {
            AutoAddSubscriptions = true
        };

        expression.Message<TMessageType>();

        return expression;
    }

    public IPublishToExpression PublishAllMessages()
    {
        var expression = new PublishingExpression(this)
        {
            AutoAddSubscriptions = true
        };

        expression.AddSubscriptionForAllMessages();
        return expression;
    }

    public IListenerConfiguration LocalQueue(string queueName)
    {
        var settings = Transports.GetOrCreate<LocalTransport>().QueueFor(queueName);
        return new ListenerConfiguration(settings);
    }

    public void StubAllExternallyOutgoingEndpoints()
    {
        Advanced.StubAllOutgoingExternalSenders = true;
    }


}