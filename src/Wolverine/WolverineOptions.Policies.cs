using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Local;

namespace Wolverine;

public sealed partial class WolverineOptions : IPolicies
{
    void IPolicies.Add<T>()
    {
        this.As<IPolicies>().Add(new T());
    }

    void IPolicies.Add(IEndpointPolicy policy)
    {
        Transports.AddPolicy(policy);
    }

    void IPolicies.UseDurableInboxOnAllListeners()
    {
        this.As<IPolicies>().AllListeners(x => x.UseDurableInbox());
    }

    void IPolicies.UseDurableLocalQueues()
    {
        this.As<IPolicies>().AllLocalQueues(q => q.UseDurableInbox());
    }

    void IPolicies.UseDurableOutboxOnAllSendingEndpoints()
    {
        this.As<IPolicies>().AllSenders(x => x.UseDurableOutbox());
    }

    void IPolicies.AllListeners(Action<ListenerConfiguration> configure)
    {
        var policy = new LambdaEndpointPolicy<Endpoint>((e, runtime) =>
        {
            if (e.Role == EndpointRole.System)
            {
                return;
            }

            if (e is LocalQueue)
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

        Transports.AddPolicy(policy);
    }

    void IPolicies.AllSenders(Action<ISubscriberConfiguration> configure)
    {
        var policy = new LambdaEndpointPolicy<Endpoint>((e, runtime) =>
        {
            if (e is LocalQueue)
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

        Transports.AddPolicy(policy);
    }

    void IPolicies.AllLocalQueues(Action<IListenerConfiguration> configure)
    {
        var policy = new LambdaEndpointPolicy<Endpoint>((e, runtime) =>
        {
            if (e is LocalQueue local)
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

        Transports.AddPolicy(policy);
    }

    LocalMessageRoutingConvention IPolicies.ConfigureConventionalLocalRouting()
    {
        return LocalRouting;
    }
}