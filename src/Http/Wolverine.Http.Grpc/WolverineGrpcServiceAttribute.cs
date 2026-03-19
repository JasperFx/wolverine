namespace Wolverine.Http.Grpc;

/// <summary>
/// Marks a class as a Wolverine gRPC service for automatic discovery and registration
/// via <see cref="WolverineGrpcExtensions.MapWolverineGrpcEndpoints"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Code-first services (protobuf-net.Grpc)</strong>: Apply to a class that inherits
/// <see cref="WolverineGrpcEndpointBase"/> and implements a <c>[ServiceContract]</c> interface.
/// The attribute bypasses naming-convention discovery requirements.
/// </para>
/// <para>
/// <strong>Proto-first services (Grpc.AspNetCore + .proto files)</strong>: This attribute is
/// <strong>REQUIRED</strong> because proto-first services inherit from proto-generated base classes
/// (e.g. <c>Greeter.GreeterBase</c>) instead of <see cref="WolverineGrpcEndpointBase"/>, and C#
/// does not support multiple inheritance. Inject <see cref="Wolverine.IMessageBus"/> via constructor
/// (only if you need to use it in your methods).
/// </para>
/// <para>
/// <strong>Why proto-first can't use WolverineGrpcEndpointBase</strong>: The proto-generated base
/// class (e.g. <c>Greeter.GreeterBase</c>) contains gRPC method signatures required by ASP.NET Core's
/// gRPC infrastructure. You <strong>must</strong> inherit from it — there is no alternative. Since C#
/// doesn't allow multiple inheritance, you cannot also inherit <see cref="WolverineGrpcEndpointBase"/>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WolverineGrpcServiceAttribute : Attribute
{
}
