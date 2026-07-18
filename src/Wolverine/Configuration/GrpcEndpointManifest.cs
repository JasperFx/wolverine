namespace Wolverine.Configuration;

/// <summary>
/// How a Wolverine gRPC service endpoint was discovered. v1 of the manifest only surfaces
/// <see cref="ProtoFirst"/> and <see cref="CodeFirst"/> endpoints (the flavors whose generated wrapper forwards
/// the request to the message bus); the remaining members exist for forward compatibility.
/// </summary>
public enum GrpcServiceDiscoveryMode
{
    /// <summary>Proto-first: an abstract stub inheriting a proto-generated service base.</summary>
    ProtoFirst,

    /// <summary>Code-first: a <c>[ServiceContract]</c> interface that Wolverine generates an implementation for.</summary>
    CodeFirst,

    /// <summary>Hand-written: a concrete service class wrapped by a generated delegation wrapper.</summary>
    HandWritten,

    /// <summary>Direct-mapped: a service mapped without a Wolverine-generated chain.</summary>
    DirectMapped
}

/// <summary>
/// The RPC cardinality of a discovered gRPC endpoint — the dimension CritterWatch needs to render the right
/// chain-detail affordance for a gRPC origin. Every kind's generated wrapper forwards the request(s) to the
/// Wolverine message bus.
/// </summary>
public enum GrpcRpcStreamKind
{
    /// <summary>Unary RPC — a single request forwarded via <c>IMessageBus.InvokeAsync</c>.</summary>
    Unary,

    /// <summary>Server-streaming RPC — a single request forwarded via <c>IMessageBus.StreamAsync</c>.</summary>
    ServerStreaming,

    /// <summary>
    /// Bidirectional-streaming RPC — each inbound request-stream item is forwarded via <c>IMessageBus.StreamAsync</c>.
    /// Only proto-first services reach this shape today.
    /// </summary>
    BidirectionalStreaming,

    /// <summary>
    /// Client-streaming RPC — the inbound request stream is forwarded as a whole via
    /// <c>IMessageBus.StreamAsync</c> for a single response. Only proto-first services reach this shape today.
    /// </summary>
    ClientStreaming
}

/// <summary>
/// A discovered gRPC endpoint and the Wolverine message type it forwards to the message bus. For proto-first and
/// code-first services the generated wrapper forwards the request to the bus, so <see cref="RequestType"/> is the
/// published Wolverine message. Unary RPCs forward via <c>IMessageBus.InvokeAsync</c>; server- and
/// bidirectional-streaming RPCs forward via <c>IMessageBus.StreamAsync</c>; client-streaming RPCs forward the whole
/// inbound stream via <c>IMessageBus.StreamAsync</c>. Hand-written and direct-mapped services
/// are excluded — Wolverine does not own their dispatch, so there is no reliable message-publishing origin to surface.
/// </summary>
/// <param name="ServiceName">The service name — the proto service's simple name for proto-first, or the contract's
/// service name for code-first (not necessarily package-qualified).</param>
/// <param name="MethodName">The RPC method name.</param>
/// <param name="RequestType">The request type — the Wolverine message forwarded to the bus. For unary and
/// server-streaming RPCs this is the request parameter; for bidirectional-streaming it is the per-item element type of
/// the inbound request stream (each item is forwarded individually); for client-streaming it is the element type of
/// the inbound stream (the actual bus message is <c>IAsyncEnumerable&lt;RequestType&gt;</c>).</param>
/// <param name="ResponseType">The response type — unwrapped from <c>Task&lt;T&gt;</c> for unary and client-streaming
/// RPCs, or the element type of the outbound response stream for server-/bidirectional-streaming RPCs; <c>null</c>
/// for a method with no typed response.</param>
/// <param name="HandlerType">The identity of the discovered service (stub or contract type).</param>
/// <param name="Mode">How the service was discovered.</param>
/// <param name="StreamKind">The RPC cardinality (unary, server-streaming, client-streaming, or bidirectional-streaming).</param>
public sealed record GrpcEndpointDescriptor(
    string ServiceName,
    string MethodName,
    Type RequestType,
    Type? ResponseType,
    Type HandlerType,
    GrpcServiceDiscoveryMode Mode,
    GrpcRpcStreamKind StreamKind);

/// <summary>
/// Exposes Wolverine's discovered gRPC unary endpoint → message-type mapping so monitoring/diagnostic consumers
/// (e.g. CritterWatch's <c>PublisherKind.GrpcEndpoint</c>) can read it without referencing <c>WolverineFx.Grpc</c>.
/// Registered by <c>Wolverine.Grpc</c>; resolves to <c>null</c> (unregistered) when the application has no gRPC.
/// </summary>
public interface IGrpcEndpointManifest
{
    /// <summary>
    /// The discovered gRPC endpoints whose generated wrapper forwards the request to the message bus — unary,
    /// server-streaming, client-streaming, and bidirectional-streaming RPCs across proto-first and code-first
    /// services. Empty when gRPC is enabled but no such services were discovered.
    /// </summary>
    IReadOnlyList<GrpcEndpointDescriptor> Endpoints { get; }
}
