using System;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;

namespace Wolverine;

public interface IPolicies : IEnumerable<IWolverinePolicy>, IWithFailurePolicies
{
    /// <summary>
    /// Add a new Wolverine policy
    /// </summary>
    /// <typeparam name="T"></typeparam>
    void Add<T>() where T : IWolverinePolicy, new();

    /// <summary>
    ///     Add a new endpoint policy
    /// </summary>
    /// <param name="policy"></param>
    void Add(IWolverinePolicy policy);

    /// <summary>
    ///     Set all non local listening endpoints to be enrolled into durable inbox
    /// </summary>
    void UseDurableInboxOnAllListeners();

    /// <summary>
    ///     Set all local queues to be enrolled into durability
    /// </summary>
    void UseDurableLocalQueues();

    /// <summary>
    ///     Set all outgoing, external endpoints to be enrolled into durable outbox sending
    /// </summary>
    void UseDurableOutboxOnAllSendingEndpoints();

    /// <summary>
    ///     Create a policy for all listening *non local* endpoints
    /// </summary>
    /// <param name="configure"></param>
    void AllListeners(Action<ListenerConfiguration> configure);

    /// <summary>
    ///     Create a policy for all *non local* subscriber endpoints
    /// </summary>
    /// <param name="configure"></param>
    void AllSenders(Action<ISubscriberConfiguration> configure);

    /// <summary>
    ///     Create a policy for all local queues
    /// </summary>
    /// <param name="configure"></param>
    void AllLocalQueues(Action<IListenerConfiguration> configure);

    /// <summary>
    /// Override the routing for locally handled messages
    /// </summary>
    /// <returns></returns>
    ILocalMessageRoutingConvention ConfigureConventionalLocalRouting();

    /// <summary>
    /// Opt out of Wolverine's default convention of routing messages to the local node's queues
    /// Use this to force messages without explicit routing rules to be sent to external transports
    /// even if the node has a message handler for the message type
    /// </summary>
    void DisableConventionalLocalRouting();

    
    /// <summary>
    /// In place of using [Transactional] attributes, apply transactional middleware
    /// to every message handler that uses transactional services
    /// </summary>
    void AutoApplyTransactions();
    
    /// <summary>
    ///     Add middleware only on handlers where the message type can be cast to the message
    ///     type of the middleware type
    /// </summary>
    /// <param name="middlewareType"></param>
    void AddMiddlewareByMessageType(Type middlewareType);

    /// <summary>
    ///     Add Wolverine middleware to message handlers
    /// </summary>
    /// <param name="filter">If specified, limits the applicability of the middleware to certain message types</param>
    /// <typeparam name="T">The actual middleware type</typeparam>
    void AddMiddleware<T>(Func<HandlerChain, bool>? filter = null);

    /// <summary>
    ///     Add Wolverine middleware to message handlers
    /// </summary>
    /// <param name="middlewareType">The actual middleware type</param>
    /// <param name="filter">If specified, limits the applicability of the middleware to certain message types</param>
    void AddMiddleware(Type middlewareType, Func<HandlerChain, bool>? filter = null);

    /// <summary>
    /// For the purposes of interoperability with NServiceBus or MassTransit, register
    /// the assemblies for shared message types to make Wolverine try to forward the message
    /// names of its messages to the interfaces of NServiceBus or MassTransit message types
    /// </summary>
    /// <param name="assembly"></param>
    void RegisterInteropMessageAssembly(Assembly assembly);

    /// <summary>
    /// Specify that the following members on every message that can be cast
    /// to type T should be audited as part of telemetry, logging, and metrics
    /// data exported from this application
    /// </summary>
    /// <param name="members"></param>
    /// <typeparam name="T"></typeparam>
    void Audit<T>(params Expression<Func<T, object>>[] members);

    /// <summary>
    /// Write a log message with the given log level when message execution starts.
    /// This would also include any audited members of the message
    /// </summary>
    /// <param name="logLevel"></param>
    void LogMessageStarting(LogLevel logLevel);
}