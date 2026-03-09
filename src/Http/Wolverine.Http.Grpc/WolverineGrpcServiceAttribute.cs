namespace Wolverine.Http.Grpc;

/// <summary>
/// Marks a class as a Wolverine gRPC service endpoint to be discovered and registered automatically.
/// Classes decorated with this attribute will be registered as gRPC services when
/// <see cref="WolverineGrpcExtensions.MapWolverineGrpcEndpoints"/> is called.
/// </summary>
/// <remarks>
/// Apply this attribute to classes that inherit from <see cref="WolverineGrpcEndpointBase"/>
/// and implement a gRPC service contract interface decorated with [ServiceContract].
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WolverineGrpcServiceAttribute : Attribute
{
}
