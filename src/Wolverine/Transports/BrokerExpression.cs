using System;
using System.Linq;
using Baseline;
using Wolverine.Configuration;

namespace Wolverine.Transports;

public abstract class BrokerExpression<TTransport, TListenerEndpoint, TSubscriberEndpoint, TListenerExpression, TSubscriber, TSelf> 
    where TSelf : BrokerExpression<TTransport, TListenerEndpoint, TSubscriberEndpoint, TListenerExpression, TSubscriber, TSelf> 
    where TTransport : IBrokerTransport
    where TListenerEndpoint : Endpoint
    where TSubscriberEndpoint : Endpoint
{
    protected BrokerExpression(TTransport transport, WolverineOptions options)
    {
        Transport = transport;
        Options = options;
    }

    protected internal TTransport Transport { get; }

    protected internal WolverineOptions Options { get; }
    
    // TODO -- both options with environment = Development

    /// <summary>
    /// All Rabbit MQ exchanges, queues, and bindings should be declared at runtime by Wolverine.
    /// </summary>
    /// <returns></returns>
    public TSelf AutoProvision()
    {
        Transport.AutoProvision = true;
        return this.As<TSelf>();
    }

    /// <summary>
    /// All queues should be purged of existing messages on first usage
    /// </summary>
    /// <returns></returns>
    public TSelf AutoPurgeOnStartup()
    {
        Transport.AutoPurgeAllQueues = true;
        return this.As<TSelf>();
    }
    
    

    /// <summary>
    /// Apply a policy to all Rabbit MQ listening endpoints
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public TSelf ConfigureListeners(Action<TListenerExpression> configure)
    {
        var policy = new LambdaEndpointPolicy<TListenerEndpoint>((e, runtime) =>
        {
            if (e.Role == EndpointRole.System) return;
            if (!e.IsListener) return;

            var configuration = createListenerExpression(e);
            configure(configuration);

            configuration.As<IDelayedEndpointConfiguration>().Apply();
        });
        
        Options.Policies.Add(policy);

        return this.As<TSelf>();
    }

    protected abstract TListenerExpression createListenerExpression(TListenerEndpoint listenerEndpoint);

    /// <summary>
    /// Apply a policy to all Rabbit MQ listening endpoints
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public TSelf ConfigureSenders(Action<TSubscriber> configure)
    {
        var policy = new LambdaEndpointPolicy<TSubscriberEndpoint>((e, runtime) =>
        {
            if (e.Role == EndpointRole.System) return;
            if (!e.Subscriptions.Any()) return;

            var configuration = createSubscriberExpression(e);
            configure(configuration);

            configuration.As<IDelayedEndpointConfiguration>().Apply();
        });
        
        Options.Policies.Add(policy);

        return this.As<TSelf>();
    }

    protected abstract TSubscriber createSubscriberExpression(TSubscriberEndpoint subscriberEndpoint);
}