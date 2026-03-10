namespace Wolverine.Http.Grpc;

/// <summary>
/// Marks a class as a Wolverine gRPC service for automatic discovery and registration
/// via <see cref="WolverineGrpcExtensions.MapWolverineGrpcEndpoints"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Code-first services (protobuf-net.Grpc)</strong>: Apply to a class that inherits
/// <see cref="WolverineGrpcEndpointBase"/> and implements a <c>[ServiceContract]</c> interface.
/// </para>
/// <para>
/// <strong>Proto-first services (Grpc.AspNetCore + .proto files)</strong>: Apply to a class that
/// inherits a proto-generated base class (e.g. <c>Greeter.GreeterBase</c>) and injects
/// <see cref="Wolverine.IMessageBus"/> via the constructor.
/// <see cref="WolverineGrpcEndpointBase"/> is <strong>not</strong> required when this attribute is present.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WolverineGrpcServiceAttribute : Attribute
{
}
