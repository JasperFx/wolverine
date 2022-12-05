using System;
using System.Linq;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Local;

namespace Wolverine;

internal class EndpointPolicies : IEndpointPolicies
{
    private readonly TransportCollection _endpoints;
    private readonly WolverineOptions _wolverineOptions;

    public EndpointPolicies(TransportCollection endpoints, WolverineOptions wolverineOptions)
    {
        _endpoints = endpoints;
        _wolverineOptions = wolverineOptions;
    }

    public void Add<T>() where T : IEndpointPolicy, new()
    {
        Add(new T());
    }

    public void Add(IEndpointPolicy policy)
    {
        _endpoints.AddPolicy(policy);
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
            if (e.Role == EndpointRole.System)
            {
                return;
            }

            if (e is LocalQueueSettings)
            {
                return;
            }

            if (!e.IsListener)
            {
                return;
            }

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
            if (e is LocalQueueSettings)
            {
                return;
            }

            if (e.Role == EndpointRole.System)
            {
                return;
            }

            if (!e.Subscriptions.Any())
            {
                return;
            }

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
                if (e.Role == EndpointRole.System)
                {
                    return;
                }

                var configuration = new ListenerConfiguration(local);
                configure(configuration);

                configuration.As<IDelayedEndpointConfiguration>().Apply();
            }
        });

        _endpoints.AddPolicy(policy);
    }

    public LocalMessageRoutingConvention UseConventionalLocalRouting()
    {
        var convention = new LocalMessageRoutingConvention();
        _wolverineOptions.RoutingConventions.Add(convention);

        return convention;
    }
}