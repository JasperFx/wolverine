using Wolverine.Runtime;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Base class for code-first gRPC service endpoints using protobuf-net.Grpc.
/// Provides <see cref="Bus"/> property for message bus access via property injection.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Code-first services</strong>: Inherit this base class and implement your
/// <c>[ServiceContract]</c> interface. The <see cref="Bus"/> property is automatically
/// injected by ASP.NET Core's DI container — no constructor needed. Convention-based
/// discovery requires the class name to end with "GrpcEndpoint", "GrpcEndpoints",
/// "GrpcService", or "GrpcServices" unless <see cref="WolverineGrpcServiceAttribute"/> is also applied.
/// </para>
/// <para>
/// <strong>Proto-first services CANNOT use this base class</strong>: Proto-first services must
/// inherit from proto-generated base classes (e.g. <c>Greeter.GreeterBase</c>) which are required
/// by ASP.NET Core's gRPC infrastructure. Since C# does not allow multiple inheritance, you cannot
/// inherit both. Instead, apply <see cref="WolverineGrpcServiceAttribute"/> and inject
/// <see cref="IMessageBus"/> via constructor when needed.
/// </para>
/// </remarks>
public abstract class WolverineGrpcEndpointBase
{
    /// <summary>
    /// Gets the Wolverine message bus for dispatching commands and queries.
    /// Set by the DI container when the service is resolved.
    /// </summary>
    public IMessageBus Bus { get; set; } = null!;
}
