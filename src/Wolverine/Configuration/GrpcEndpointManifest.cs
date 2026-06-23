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
/// A discovered unary gRPC endpoint and the Wolverine message type it forwards to the message bus. For proto-first
/// and code-first services the generated wrapper forwards the request (the first parameter) to
/// <c>IMessageBus.InvokeAsync</c>, so <see cref="RequestType"/> is the published Wolverine message.
/// </summary>
/// <param name="ServiceName">The service name — the proto service's simple name for proto-first, or the contract's
/// service name for code-first (not necessarily package-qualified).</param>
/// <param name="MethodName">The unary RPC method name.</param>
/// <param name="RequestType">The request type — the Wolverine message forwarded to the bus.</param>
/// <param name="ResponseType">The response type, or <c>null</c> for a method with no typed response.</param>
/// <param name="HandlerType">The identity of the discovered service (stub or contract type).</param>
/// <param name="Mode">How the service was discovered.</param>
public sealed record GrpcEndpointDescriptor(
    string ServiceName,
    string MethodName,
    Type RequestType,
    Type? ResponseType,
    Type HandlerType,
    GrpcServiceDiscoveryMode Mode);

/// <summary>
/// Exposes Wolverine's discovered gRPC unary endpoint → message-type mapping so monitoring/diagnostic consumers
/// (e.g. CritterWatch's <c>PublisherKind.GrpcEndpoint</c>) can read it without referencing <c>WolverineFx.Grpc</c>.
/// Registered by <c>Wolverine.Grpc</c>; resolves to <c>null</c> (unregistered) when the application has no gRPC.
/// </summary>
public interface IGrpcEndpointManifest
{
    /// <summary>
    /// The discovered unary gRPC endpoints. Empty when gRPC is enabled but no services were discovered.
    /// </summary>
    IReadOnlyList<GrpcEndpointDescriptor> Endpoints { get; }
}
