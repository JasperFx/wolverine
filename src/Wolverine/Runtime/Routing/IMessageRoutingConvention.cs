using Wolverine.Configuration;

namespace Wolverine.Runtime.Routing;

#region sample_imessageroutingconvention
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

    /// <summary>
    /// Eagerly register subscription metadata and apply sender configuration
    /// for the given handled message types, BEFORE any
    /// <c>BrokerTransport.InitializeAsync</c> compiles the endpoints. This
    /// gives endpoint policies like <c>UseDurableOutboxOnAllSendingEndpoints</c>
    /// a chance to see <c>Subscriptions.Any() == true</c> on conventionally-
    /// routed sender endpoints when their first <c>Compile()</c> runs. Unlike
    /// <see cref="DiscoverSenders"/>, this MUST NOT build the sending agent
    /// — the broker is not yet connected at this phase of host startup.
    ///
    /// Default no-op so custom <see cref="IMessageRoutingConvention"/>
    /// implementations are unaffected. The built-in
    /// <c>MessageRoutingConvention&lt;,,,&gt;</c> base class overrides this.
    /// See https://github.com/JasperFx/wolverine/issues/2588.
    /// </summary>
    void PreregisterSenders(IReadOnlyList<Type> handledMessageTypes, IWolverineRuntime runtime)
    {
    }
}

#endregion