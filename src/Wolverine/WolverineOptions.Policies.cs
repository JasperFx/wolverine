using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Middleware;
using Wolverine.Persistence;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Local;
using Wolverine.Util;

namespace Wolverine;

public sealed partial class WolverineOptions : IPolicies
{
    internal List<IWolverinePolicy> RegisteredPolicies { get; } = new();



    void IPolicies.AutoApplyTransactions()
    {
        this.As<IPolicies>().Add(new AutoApplyTransactions());
    }

    void IPolicies.Add<T>()
    {
        this.As<IPolicies>().Add(new T());
    }

    void IPolicies.Add(IWolverinePolicy policy)
    {
        if (policy is IEndpointPolicy e) Transports.AddPolicy(e);
        RegisteredPolicies.Add(policy);
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

    ILocalMessageRoutingConvention IPolicies.ConfigureConventionalLocalRouting()
    {
        return Transports.GetOrCreate<LocalTransport>();
    }
    
    void IPolicies.DisableConventionalLocalRouting()
    {
        LocalRoutingConventionDisabled = true;
    }
    
    internal MiddlewarePolicy FindOrCreateMiddlewarePolicy()
    {
        var policy = RegisteredPolicies.OfType<MiddlewarePolicy>().FirstOrDefault();
        if (policy == null)
        {
            policy = new MiddlewarePolicy();
            RegisteredPolicies.Add(policy);
        }

        return policy;
    }

    void IPolicies.AddMiddleware(Type middlewareType, Func<HandlerChain, bool>? filter = null)
    {
        FindOrCreateMiddlewarePolicy().AddType(middlewareType, chain =>
        {
            if (filter == null) return true;

            if (chain is HandlerChain c)
            {
                return filter(c);
            }

            return false;
        });
    }

    void IPolicies.AddMiddleware<T>(Func<HandlerChain, bool>? filter = null)
    {
        this.As<IPolicies>().AddMiddleware(typeof(T), filter);
    }

    IEnumerator<IWolverinePolicy> IEnumerable<IWolverinePolicy>.GetEnumerator()
    {
        return RegisteredPolicies.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return RegisteredPolicies.GetEnumerator();
    }

    FailureRuleCollection IWithFailurePolicies.Failures => HandlerGraph.Failures;
    
    
    /// <summary>
    ///     For the purposes of interoperability with NServiceBus or MassTransit, register
    ///     the assemblies for shared message types to make Wolverine try to forward the message
    ///     names of its messages to the interfaces of NServiceBus or MassTransit message types
    /// </summary>
    /// <param name="assembly"></param>
    void IPolicies.RegisterInteropMessageAssembly(Assembly assembly)
    {
        HandlerGraph.InteropAssemblies.Add(assembly);
        WolverineMessageNaming.AddMessageInterfaceAssembly(assembly);
    }

    MessageTypePolicies<T> IPolicies.ForMessagesOfType<T>()
    {
        return new MessageTypePolicies<T>(this);
    }

    public void LogMessageStarting(LogLevel logLevel)
    {
        RegisteredPolicies.Insert(0, new LogStartingActivityPolicy(logLevel));
    }
}