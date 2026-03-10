namespace Wolverine.Http.Grpc;

/// <summary>
/// Marks a class as a Wolverine gRPC service endpoint to be discovered and registered automatically.
/// Classes decorated with this attribute will be registered as gRPC services when
/// <see cref="WolverineGrpcExtensions.MapWolverineGrpcEndpoints"/> is called.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Code-first services (protobuf-net.Grpc)</strong>: Apply this attribute to a class that
/// inherits from <see cref="WolverineGrpcEndpointBase"/> and implements a gRPC service contract
/// interface decorated with <c>[ServiceContract]</c>.
/// </para>
/// <para>
/// <strong>Proto-first services (Grpc.AspNetCore + .proto files)</strong>: Apply this attribute
/// to a class that inherits from the proto-generated base class (e.g. <c>Greeter.GreeterBase</c>)
/// and injects <see cref="Wolverine.IMessageBus"/> via the constructor.
/// <c>WolverineGrpcEndpointBase</c> is <strong>not required</strong> when this attribute is present —
/// the attribute alone is sufficient for automatic discovery.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WolverineGrpcServiceAttribute : Attribute
{
}
