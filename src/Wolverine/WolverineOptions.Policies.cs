using System.Collections;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Middleware;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Local;
using Wolverine.Util;

namespace Wolverine;

/// <summary>
/// Extension point to help forward concrete message types to the correct
/// message type that Wolverine handles
/// </summary>
public interface IHandledTypeRule
{
    bool TryFindHandledType(Type concreteType, out Type handlerType);
}

public sealed partial class WolverineOptions : IPolicies
{
    internal List<IWolverinePolicy> RegisteredPolicies { get; } = [new TagHandlerPolicy()];

    internal bool PublishAgentEvents { get; set; } 
    
    bool IPolicies.PublishAgentEvents
    {
        get => PublishAgentEvents;
        set => PublishAgentEvents = value;
    }


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
        if (policy is IEndpointPolicy e)
        {
            Transports.AddPolicy(e);
        }

        RegisteredPolicies.Add(policy);
    }

    void IPolicies.UseDurableInboxOnAllListeners()
    {
        this.As<IPolicies>().AllListeners(x => x.UseDurableInbox());
    }

    internal readonly List<IHandledTypeRule> HandledTypeRules = [new AgentCommandHandledTypeRule()];

    void IPolicies.ForwardHandledTypes(IHandledTypeRule rule)
    {
        HandledTypeRules.Add(rule);
    }

    void IPolicies.ConventionalLocalRoutingIsAdditive()
    {
        InternalRouteSources.OfType<LocalRouting>().Single().IsAdditive = true;
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
        var policy = new LambdaEndpointPolicy<Endpoint>((e, _) =>
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
        var policy = new LambdaEndpointPolicy<Endpoint>((e, _) =>
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
        var policy = new LambdaEndpointPolicy<Endpoint>((e, _) =>
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

#pragma warning disable CS1066
    void IPolicies.AddMiddleware(Type middlewareType, Func<HandlerChain, bool>? filter = null)
#pragma warning restore CS1066
    {
        filter ??= _ => true;

        FindOrCreateMiddlewarePolicy().AddType(middlewareType, chain =>
        {
            if (chain is HandlerChain c)
            {
                return filter(c);
            }

            return false;
        });
    }

#pragma warning disable CS1066
    void IPolicies.AddMiddleware<T>(Func<HandlerChain, bool>? filter = null)
#pragma warning restore CS1066
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

    /// <summary>
    /// Logger level for Wolverine to use to log the successful processing of a message. The
    /// default is Information
    /// </summary>
    /// <param name="logLevel"></param>
    void IPolicies.MessageSuccessLogLevel(LogLevel logLevel)
    {
        var policy = new LambdaHandlerPolicy(c => c.SuccessLogLevel = logLevel);
        Policies.Add(policy);
    }

    /// <summary>
    /// Logger level for Wolverine to use for log messages marking the execution stop and finish for all
    /// messages being processed. Wolverine's default is Debug
    /// </summary>
    /// <param name="logLevel"></param>
    void IPolicies.MessageExecutionLogLevel(LogLevel logLevel)
    {
        var policy = new LambdaHandlerPolicy(c => c.ProcessingLogLevel = logLevel);
        Policies.Add(policy);
    }

    void IPolicies.LogMessageStarting(LogLevel logLevel)
    {
        RegisteredPolicies.Insert(0, new LogStartingActivityPolicy(logLevel));
    }

    internal MiddlewarePolicy FindOrCreateMiddlewarePolicy()
    {
        var policy = RegisteredPolicies.OfType<MiddlewarePolicy>().FirstOrDefault();
        if (policy == null)
        {
            policy = new MiddlewarePolicy();
            RegisteredPolicies.Insert(0, policy);
        }

        return policy;
    }
}

internal class LambdaHandlerPolicy : IHandlerPolicy
{
    private readonly Action<HandlerChain> _configure;

    public LambdaHandlerPolicy(Action<HandlerChain> configure)
    {
        _configure = configure;
    }

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            _configure(chain);
        }
    }
}