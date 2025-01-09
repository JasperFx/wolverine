using Wolverine.Configuration;

namespace Wolverine.Runtime.Routing;

#region sample_IMessageRoutingConvention

/// <summary>
///     Plugin for doing any kind of conventional message routing
/// </summary>
public interface IMessageRoutingConvention
{
    /// <summary>
    /// Use this to define listening endpoints based on the known message handlers for the application
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="handledMessageTypes"></param>
    void DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes);
    
    /// <summary>
    /// Create outgoing subscriptions for the application for the given message type
    /// </summary>
    /// <param name="messageType"></param>
    /// <param name="runtime"></param>
    /// <returns></returns>
    IEnumerable<Endpoint> DiscoverSenders(Type messageType, IWolverineRuntime runtime);
}

#endregion