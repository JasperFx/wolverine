namespace Wolverine.Http.Grpc;

/// <summary>
/// Configuration options for Wolverine gRPC integration.
/// </summary>
public class WolverineGrpcOptions
{
    /// <summary>
    /// Additional assemblies to scan for Wolverine gRPC service endpoint types.
    /// By default, Wolverine will scan the same assemblies configured for
    /// handler and HTTP endpoint discovery.
    /// </summary>
    public List<System.Reflection.Assembly> Assemblies { get; } = [];
}
