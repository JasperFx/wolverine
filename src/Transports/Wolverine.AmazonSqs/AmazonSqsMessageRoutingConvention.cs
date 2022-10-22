using Baseline;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Util;

namespace Wolverine.AmazonSqs;

public class AmazonSqsMessageRoutingConvention : IMessageRoutingConvention
{
    private Func<Type, string?> _queueNameForListener = t => t.ToMessageTypeName().Replace(".", "-");
    private Func<Type, string?> _queueNameForSender = t => t.ToMessageTypeName().Replace(".", "-");
    private Action<AmazonSqsListenerConfiguration,MessageRoutingContext> _configureListener = (_, _) => {};
    private Action<AmazonSqsSubscriberConfiguration,MessageRoutingContext> _configureSending = (_, _) => {};

    /// <summary>
    /// Optionally include (allow list) or exclude (deny list) types. By default, this will apply to all message types
    /// </summary>
    internal CompositeFilter<Type> TypeFilters { get; } = new();

    /// <summary>
    /// Create an allow list of included message types. This is accumulative.
    /// </summary>
    /// <param name="filter"></param>
    public void IncludeTypes(Func<Type, bool> filter)
    {
        TypeFilters.Includes.Add(filter);
    }
        
    /// <summary>
    /// Create an deny list of included message types. This is accumulative.
    /// </summary>
    /// <param name="filter"></param>
    public void ExcludeTypes(Func<Type, bool> filter)
    {
        TypeFilters.Excludes.Add(filter);
    }

    void IMessageRoutingConvention.DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
    {
        var transport = runtime.Options.AmazonSqsTransport();

        foreach (var messageType in handledMessageTypes.Where(t => TypeFilters.Matches(t)))
        {
            // Can be null, so bail out if there's no queue
            var queueName = _queueNameForListener(messageType);
            if (queueName.IsEmpty()) return;

            var endpoint = transport.EndpointForQueue(queueName);
            endpoint.IsListener = true;

            var context = new MessageRoutingContext(messageType, runtime);
            var configuration = new AmazonSqsListenerConfiguration(endpoint);
                
            _configureListener(configuration, context);
                
            configuration.As<IDelayedEndpointConfiguration>().Apply();
        }
    }

    IEnumerable<Endpoint> IMessageRoutingConvention.DiscoverSenders(Type messageType, IWolverineRuntime runtime)
    {
        if (!TypeFilters.Matches(messageType)) yield break;
            
        var transport = runtime.Options.AmazonSqsTransport();

        var queueName = _queueNameForSender(messageType);
        if (queueName.IsEmpty()) yield break;
        var queue = transport.Queues[queueName];

        var configuration = new AmazonSqsSubscriberConfiguration(queue);
            
        _configureSending(configuration, new MessageRoutingContext(messageType, runtime));

        configuration.As<IDelayedEndpointConfiguration>().Apply();
            
        // This will start up the sending agent
        var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(queue.Uri);
        yield return sendingAgent.Endpoint;
    }
    
    /// <summary>
    /// Override the convention for determining the queue name for receiving incoming messages of the message type.
    /// Returning null or empty is interpreted as "don't create a new queue for this message type". Default is the MessageTypeName
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public AmazonSqsMessageRoutingConvention QueueNameForListener(Func<Type, string?> nameSource)
    {
        _queueNameForListener = nameSource ?? throw new ArgumentNullException(nameof(nameSource));
        return this;
    }
    
    /// <summary>
    /// Override the convention for determining the queue name for receiving incoming messages of the message type.
    /// Returning null or empty is interpreted as "don't create a new queue for this message type". Default is the MessageTypeName
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public AmazonSqsMessageRoutingConvention QueueNameForSender(Func<Type, string?> nameSource)
    {
        _queueNameForSender = nameSource ?? throw new ArgumentNullException(nameof(nameSource));
        return this;
    }
    
    /// <summary>
    /// Override the Rabbit MQ and Wolverine configuration for new listening endpoints created by message type.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsMessageRoutingConvention ConfigureListeners(Action<AmazonSqsListenerConfiguration, MessageRoutingContext> configure)
    {
        _configureListener = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>
    /// Override the Rabbit MQ and Wolverine configuration for sending endpoints, exchanges, and queue bindings
    /// for a new sending endpoint
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsMessageRoutingConvention ConfigureSending(Action<AmazonSqsSubscriberConfiguration, MessageRoutingContext> configure)
    {
        _configureSending = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }
}