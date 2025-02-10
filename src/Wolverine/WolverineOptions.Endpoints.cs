using JasperFx.Core.Descriptions;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine;

public partial class WolverineOptions : IAsyncDisposable
{
    internal List<IMessageRoutingConvention> RoutingConventions { get; } = new();

    /// <summary>
    ///     Configure the properties of the default, local queue
    /// </summary>
    [IgnoreDescription]
    public LocalQueueConfiguration DefaultLocalQueue => LocalQueue(TransportConstants.Default);

    /// <summary>
    ///     Configure the properties of the default, durable local queue
    /// </summary>
    [IgnoreDescription]
    public LocalQueueConfiguration DurableScheduledMessagesLocalQueue => LocalQueue(TransportConstants.Durable);


    ValueTask IAsyncDisposable.DisposeAsync()
    {
        return Transports.As<IAsyncDisposable>().DisposeAsync();
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

    /// <summary>
    ///     Configure a potentially complex publishing/routing/subscription rule
    ///     to one or more messaging endpoints
    /// </summary>
    /// <param name="configuration"></param>
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

    /// <summary>
    ///     Start a publishing rule for all possible messages
    /// </summary>
    /// <returns></returns>
    public IPublishToExpression PublishAllMessages()
    {
        var expression = new PublishingExpression(this)
        {
            AutoAddSubscriptions = true
        };

        expression.AddSubscriptionForAllMessages();
        return expression;
    }

    /// <summary>
    ///     Configure a local queue by name. The local queue will be created if it does not already exist
    /// </summary>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public LocalQueueConfiguration LocalQueue(string queueName)
    {
        var settings = Transports.GetOrCreate<LocalTransport>().QueueFor(queueName);
        return new LocalQueueConfiguration(settings);
    }

    /// <summary>
    ///     Configure the local queue that handles the message type T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public LocalQueueConfiguration LocalQueueFor<T>()
    {
        return LocalQueueFor(typeof(T));
    }

    /// <summary>
    ///     Configure the local queue that handles the given message type
    /// </summary>
    /// <returns></returns>
    public LocalQueueConfiguration LocalQueueFor(Type messageType)
    {
        return LocalRouting.ConfigureQueueFor(messageType);
    }

    /// <summary>
    ///     For testing mode, this directs Wolverine to stub out all outbound message sending
    ///     or inbound listening from message brokers
    /// </summary>
    public void StubAllExternalTransports()
    {
        ExternalTransportsAreStubbed = true;
    }
}