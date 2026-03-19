using ProtoBuf;
using ProtoBuf.Grpc;
using System.ServiceModel;
using Wolverine;
using Wolverine.Http.Grpc;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Bootstrapping integration test fixtures
/// Used by grpc_endpoint_bootstrapping to verify AddWolverineGrpc() +
/// MapWolverineGrpcEndpoints() register a real code-first gRPC service.
/// </summary>
[ProtoContract]
public class BootstrapPingRequest
{
    [ProtoMember(1)]
    public string Message { get; set; } = "";
}

[ProtoContract]
public class BootstrapPingResponse
{
    [ProtoMember(1)]
    public string Reply { get; set; } = "";
}

/// <summary>
/// Service contract with a deliberately unique method name ("PingAsync") so that
/// bootstrapping integration tests can assert its gRPC route was registered
/// without ambiguity when other fixture types are also discovered.
/// </summary>
[ServiceContract]
public interface IBootstrapPingContract
{
    [OperationContract]
    Task<BootstrapPingResponse> PingAsync(BootstrapPingRequest request, CallContext context = default);
}

/// <summary>
/// Concrete endpoint discovered by [WolverineGrpcService]. Used in bootstrapping
/// integration tests to verify that MapWolverineGrpcEndpoints() maps the PingAsync route.
/// Uses property injection via WolverineGrpcEndpointBase (the code-first default pattern).
/// </summary>
[WolverineGrpcService]
public class BootstrapAttributedGrpcService : WolverineGrpcEndpointBase, IBootstrapPingContract
{
    public Task<BootstrapPingResponse> PingAsync(BootstrapPingRequest request, CallContext context = default)
        => Task.FromResult(new BootstrapPingResponse { Reply = $"pong: {request.Message}" });
}

// ---------------------------------------------------------------------------
// Constructor-injection fixture (code-first, no base class)
//
// Demonstrates that [WolverineGrpcService] alone is sufficient for discovery —
// WolverineGrpcEndpointBase is NOT required when using constructor DI.
// This is the recommended pattern for any service (code-first OR proto-first)
// that prefers constructor injection over the Bus property.
// ---------------------------------------------------------------------------

/// <summary>
/// Service contract used by the constructor-injection bootstrapping fixtures.
/// The deliberately unique method name "CtorPingAsync" lets route assertions
/// distinguish this service from BootstrapAttributedGrpcService (which uses "PingAsync").
/// </summary>
[ServiceContract]
public interface IBootstrapCtorContract
{
    [OperationContract]
    Task<BootstrapPingResponse> CtorPingAsync(BootstrapPingRequest request, CallContext context = default);
}

/// <summary>
/// Code-first gRPC endpoint that uses constructor injection of <see cref="IMessageBus"/>
/// instead of inheriting <see cref="WolverineGrpcEndpointBase"/>.
/// The <c>[WolverineGrpcService]</c> attribute is the only requirement for auto-discovery.
/// </summary>
[WolverineGrpcService]
public class BootstrapCtorInjectedGrpcService : IBootstrapCtorContract
{
    private readonly IMessageBus _bus;

    public BootstrapCtorInjectedGrpcService(IMessageBus bus) => _bus = bus;

    public Task<BootstrapPingResponse> CtorPingAsync(BootstrapPingRequest request, CallContext context = default)
        => _bus.InvokeAsync<BootstrapPingResponse>(request, context.CancellationToken);
}

// ---------------------------------------------------------------------------
// Type-discovery fixtures (used by grpc_endpoint_type_discovery unit tests)
//
// These types are PUBLIC so that IsGrpcEndpointType (which checks type.IsPublic)
// can return the correct eligibility result.  They are also wired up to
// ITypeDiscoverySharedContract so that if the integration tests discover them via
// assembly scanning, MapGrpcService<T>() registers valid (if duplicate) gRPC routes
// rather than failing or producing warnings about missing service contracts.
// ---------------------------------------------------------------------------

[ProtoContract]
public class TypeDiscoveryRequest
{
    [ProtoMember(1)]
    public string Data { get; set; } = "";
}

[ProtoContract]
public class TypeDiscoveryResponse
{
    [ProtoMember(1)]
    public string Result { get; set; } = "";
}

/// <summary>
/// Shared service contract for the type-discovery test fixtures.
/// Using a single shared interface avoids the need for five separate interface
/// declarations while still giving every fixture a well-formed gRPC service contract.
/// Multiple implementations of the same interface register duplicate routes in
/// ASP.NET Core, which is legal and produces no startup error.
/// </summary>
[ServiceContract]
public interface ITypeDiscoverySharedContract
{
    [OperationContract]
    Task<TypeDiscoveryResponse> ProcessAsync(TypeDiscoveryRequest request, CallContext context = default);
}

// --- Valid fixtures (IsGrpcEndpointType should return true) ---

/// <summary>Valid: has [WolverineGrpcService] attribute; no naming-convention suffix needed.</summary>
[WolverineGrpcService]
public class TypeDiscovery_ValidByAttribute : WolverineGrpcEndpointBase, ITypeDiscoverySharedContract
{
    public Task<TypeDiscoveryResponse> ProcessAsync(TypeDiscoveryRequest request, CallContext context = default)
        => Task.FromResult(new TypeDiscoveryResponse());
}

/// <summary>Valid: class name ends with "GrpcEndpoint" (naming convention).</summary>
public class TypeDiscovery_ValidGrpcEndpoint : WolverineGrpcEndpointBase, ITypeDiscoverySharedContract
{
    public Task<TypeDiscoveryResponse> ProcessAsync(TypeDiscoveryRequest request, CallContext context = default)
        => Task.FromResult(new TypeDiscoveryResponse());
}

/// <summary>Valid: class name ends with "GrpcEndpoints" (naming convention).</summary>
public class TypeDiscovery_ValidGrpcEndpoints : WolverineGrpcEndpointBase, ITypeDiscoverySharedContract
{
    public Task<TypeDiscoveryResponse> ProcessAsync(TypeDiscoveryRequest request, CallContext context = default)
        => Task.FromResult(new TypeDiscoveryResponse());
}

/// <summary>Valid: class name ends with "GrpcService" (naming convention).</summary>
public class TypeDiscovery_ValidGrpcService : WolverineGrpcEndpointBase, ITypeDiscoverySharedContract
{
    public Task<TypeDiscoveryResponse> ProcessAsync(TypeDiscoveryRequest request, CallContext context = default)
        => Task.FromResult(new TypeDiscoveryResponse());
}

/// <summary>Valid: class name ends with "GrpcServices" (naming convention).</summary>
public class TypeDiscovery_ValidGrpcServices : WolverineGrpcEndpointBase, ITypeDiscoverySharedContract
{
    public Task<TypeDiscoveryResponse> ProcessAsync(TypeDiscoveryRequest request, CallContext context = default)
        => Task.FromResult(new TypeDiscoveryResponse());
}

// --- Invalid fixtures (IsGrpcEndpointType should return false) ---

/// <summary>
/// Invalid: inherits WolverineGrpcEndpointBase, but has no attribute AND the class name
/// does not end with any recognised convention suffix.
/// </summary>
public class TypeDiscovery_InvalidNoMatch : WolverineGrpcEndpointBase, ITypeDiscoverySharedContract
{
    public Task<TypeDiscoveryResponse> ProcessAsync(TypeDiscoveryRequest request, CallContext context = default)
        => Task.FromResult(new TypeDiscoveryResponse());
}

/// <summary>
/// Valid (proto-first pattern): has [WolverineGrpcService] attribute but does NOT inherit
/// WolverineGrpcEndpointBase. This simulates a proto-first gRPC service that inherits the
/// proto-generated base class (e.g., Greeter.GreeterBase) and uses constructor DI for IMessageBus.
/// The [WolverineGrpcService] attribute alone is sufficient for discovery.
/// </summary>
[WolverineGrpcService]
public class TypeDiscovery_ValidAttributeOnlyNoBaseClass : ITypeDiscoverySharedContract
{
    public Task<TypeDiscoveryResponse> ProcessAsync(TypeDiscoveryRequest request, CallContext context = default)
        => Task.FromResult(new TypeDiscoveryResponse());
}

/// <summary>
/// Invalid: has the naming-convention suffix "GrpcEndpoint" but does NOT inherit
/// WolverineGrpcEndpointBase and does NOT have [WolverineGrpcService].
/// Convention-based discovery still requires WolverineGrpcEndpointBase to avoid
/// accidentally picking up unrelated classes.
/// </summary>
public class TypeDiscovery_InvalidConventionSuffixWithoutBaseClass : ITypeDiscoverySharedContract
{
    public Task<TypeDiscoveryResponse> ProcessAsync(TypeDiscoveryRequest request, CallContext context = default)
        => Task.FromResult(new TypeDiscoveryResponse());
}

/// <summary>
/// Invalid: abstract type — even with the attribute and the correct base class,
/// abstract types are excluded because they cannot be instantiated by DI.
/// </summary>
[WolverineGrpcService]
public abstract class TypeDiscovery_InvalidAbstract : WolverineGrpcEndpointBase { }

/// <summary>
/// Invalid: interface — cannot be a service implementation.
/// Note: [WolverineGrpcService] is not applied here because the attribute's
/// AttributeUsage is restricted to class declarations.
/// </summary>
public interface ITypeDiscovery_InvalidInterface { }

/// <summary>
/// Invalid: open generic type definition — cannot be resolved by DI as a concrete type.
/// </summary>
public class TypeDiscovery_InvalidGenericGrpcEndpoint<T> : WolverineGrpcEndpointBase, ITypeDiscoverySharedContract
{
    public Task<TypeDiscoveryResponse> ProcessAsync(TypeDiscoveryRequest request, CallContext context = default)
        => Task.FromResult(new TypeDiscoveryResponse());
}
