using Wolverine.Runtime;

namespace Wolverine.Transports;

/// <summary>
/// Capability marker for transports whose underlying protocol carries the reply in the same
/// request/response exchange (e.g. HTTP). When an <see cref="Wolverine.Configuration.Endpoint"/>
/// implements this, <c>IMessageBus.InvokeAsync&lt;T&gt;</c> routed to that endpoint skips the
/// listener-loop + reply-tracker round trip (no <c>ReplyListener&lt;T&gt;</c>, no listening endpoint
/// on the sender) and instead sends the request and reads the reply envelope straight back from the
/// protocol's response slot. The routing decision is structural — <see cref="Wolverine.Runtime.Routing.MessageRoute"/>
/// detects the capability once at construction, so there is no per-message branching on the hot path.
/// See GH-2966.
/// </summary>
public interface IInlineRequestReplyEndpoint
{
    /// <summary>
    /// Send the request envelope and return the reply envelope read directly from the transport's
    /// response slot. A handler failure on the receiver is returned as a reply envelope whose Message
    /// is a <see cref="Wolverine.Runtime.RemoteInvocation.FailureAcknowledgement"/>, which the caller
    /// translates into the usual <see cref="Wolverine.Runtime.RemoteInvocation.WolverineRequestReplyException"/>.
    /// </summary>
    Task<Envelope> InvokeRemoteAsync(Envelope request, IWolverineRuntime runtime, CancellationToken cancellation);
}
