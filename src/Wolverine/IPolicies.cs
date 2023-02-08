using System;
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
    LocalMessageRoutingConvention ConfigureConventionalLocalRouting();

    
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
    ///     Configure how & where Wolverine discovers message handler classes to override or expand
    ///     the built in conventions
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    void Discovery(Action<HandlerSource> configure);

}