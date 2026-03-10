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
/// <strong>Proto-first services</strong>: Use <see cref="WolverineGrpcServiceAttribute"/>
/// with constructor injection of <see cref="IMessageBus"/> instead. Proto-first services
/// inherit from proto-generated base classes and cannot also inherit this class.
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
