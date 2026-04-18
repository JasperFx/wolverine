namespace Wolverine.Grpc;

/// <summary>
/// Marks a class as a Wolverine-managed gRPC service for automatic discovery
/// and registration. Use this attribute when the class name does not follow
/// the "GrpcService" suffix convention (e.g., on proto-generated types in M4+).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WolverineGrpcServiceAttribute : Attribute;
