using Wolverine.Configuration;

namespace Wolverine.Runtime.Routing;

public interface ILocalMessageRoutingConvention
{
    /// <summary>
    ///     Override the type to local queue naming. By default this is the MessageTypeName
    ///     to lower case invariant
    /// </summary>
    /// <param name="determineName"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    ILocalMessageRoutingConvention Named(Func<Type, string> determineName);

    /// <summary>
    ///     Customize the endpoints
    /// </summary>
    /// <param name="customization"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    ILocalMessageRoutingConvention CustomizeQueues(Action<Type, IListenerConfiguration> customization);

    /// <summary>
    ///     Name the local queue of every handler chain that is not sticky for its handler type,
    ///     <c>local://{handler type full name, lower case}</c>, even when the message type has only one
    ///     handler type. Without this, a message type with one handler type uses the message type's queue and
    ///     only moves to per handler type queues once it has a second handler type. This affects local queue
    ///     names only: conventional broker routing still names its queues and listeners as before, and
    ///     envelopes already persisted to the message type's local queue are still handled. Off by default
    /// </summary>
    /// <returns></returns>
    ILocalMessageRoutingConvention NameQueuesByHandlerType()
    {
        throw new NotSupportedException(
            $"{GetType().FullName} does not support naming local queues by handler type");
    }
}