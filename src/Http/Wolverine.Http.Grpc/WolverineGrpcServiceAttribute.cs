namespace Wolverine.Http.Grpc;

/// <summary>
/// Marks a class as a Wolverine gRPC service for automatic discovery and registration.
/// </summary>
/// <remarks>
/// This attribute enables discovery for both code-first (protobuf-net.Grpc) and
/// proto-first (Grpc.AspNetCore + .proto files) services. When present,
/// <see cref="WolverineGrpcEndpointBase"/> is not required.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WolverineGrpcServiceAttribute : Attribute
{
}
