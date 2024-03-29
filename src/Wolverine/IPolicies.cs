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
    ///     Add a new Wolverine policy
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
    ///     Override the routing for locally handled messages
    /// </summary>
    /// <returns></returns>
    ILocalMessageRoutingConvention ConfigureConventionalLocalRouting();

    /// <summary>
    ///     Opt out of Wolverine's default convention of routing messages to the local node's queues
    ///     Use this to force messages without explicit routing rules to be sent to external transports
    ///     even if the node has a message handler for the message type
    /// </summary>
    void DisableConventionalLocalRouting();


    /// <summary>
    ///     In place of using [Transactional] attributes, apply transactional middleware
    ///     to every message handler that uses transactional services
    /// </summary>
    void AutoApplyTransactions();

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
    ///     For the purposes of interoperability with NServiceBus or MassTransit, register
    ///     the assemblies for shared message types to make Wolverine try to forward the message
    ///     names of its messages to the interfaces of NServiceBus or MassTransit message types
    /// </summary>
    /// <param name="assembly"></param>
    void RegisterInteropMessageAssembly(Assembly assembly);

    /// <summary>
    ///     Write a log message with the given log level when message execution starts.
    ///     This would also include any audited members of the message
    /// </summary>
    /// <param name="logLevel"></param>
    void LogMessageStarting(LogLevel logLevel);

    /// <summary>
    ///     Specify policies for the handling of every message type that can
    ///     be cast to T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    MessageTypePolicies<T> ForMessagesOfType<T>();

    /// <summary>
    /// Override the log level for Wolverine's built in logging for messages
    /// being completed successfully. This is Information by default
    /// </summary>
    /// <param name="logLevel"></param>
    void MessageSuccessLogLevel(LogLevel logLevel);

    /// <summary>
    /// Override the log level for Wolverine to trace execution starting and
    /// finishing of the actual message execution. The default is Debug.
    /// </summary>
    /// <param name="logLevel"></param>
    void MessageExecutionLogLevel(LogLevel logLevel);

    /// <summary>
    /// Advanced usage to forward concrete message types to the actual
    /// message type in Wolverine handlers. Mostly to map concrete types
    /// to the message handler for a particular interface or abstract class
    /// </summary>
    /// <param name="rule"></param>
    void ForwardHandledTypes(IHandledTypeRule rule);
}