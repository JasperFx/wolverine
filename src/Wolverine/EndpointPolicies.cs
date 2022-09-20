using System;
using Baseline;
using Wolverine.Configuration;
using Wolverine.Transports.Local;

namespace Wolverine;

internal class EndpointPolicies : IPolicies
{
    private readonly TransportCollection _endpoints;

    public EndpointPolicies(TransportCollection endpoints)
    {
        _endpoints = endpoints;
    }

    public void UseDurableInboxOnAllListeners()
    {
        AllListeners(x => x.UseDurableInbox());
    }

    public void UseDurableLocalQueues()
    {
        AllLocalQueues(q => q.UseDurableInbox());
    }

    public void UseDurableOutboxOnAllSendingEndpoints()
    {
        AllSenders(x => x.UseDurableOutbox());
    }

    public void AllListeners(Action<ListenerConfiguration> configure)
    {
        var policy = new LambdaEndpointPolicy<Endpoint>((e, runtime) =>
        {
            if (e is LocalQueueSettings) return;

            if (!e.IsListener) return;

            var configuration = new ListenerConfiguration(e);
            configure(configuration);

            configuration.As<IDelayedEndpointConfiguration>().Apply();
        });
        
        _endpoints.AddPolicy(policy);
    }

    public void AllSenders(Action<ISubscriberConfiguration> configure)
    {
        var policy = new LambdaEndpointPolicy<Endpoint>((e, runtime) =>
        {
            if (e is LocalQueueSettings) return;

            if (e.IsListener) return;

            var configuration = new SubscriberConfiguration(e);
            configure(configuration);

            configuration.As<IDelayedEndpointConfiguration>().Apply();
        });
        
        _endpoints.AddPolicy(policy);
    }

    public void AllLocalQueues(Action<IListenerConfiguration> configure)
    {
        var policy = new LambdaEndpointPolicy<Endpoint>((e, runtime) =>
        {
            if (e is LocalQueueSettings local)
            {
                var configuration = new ListenerConfiguration(local);
                configure(configuration);

                configuration.As<IDelayedEndpointConfiguration>().Apply();
            }
        });
        
        _endpoints.AddPolicy(policy);
    }
}