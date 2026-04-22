namespace Wolverine.Grpc;

/// <summary>
/// Marks a type as a Wolverine-managed gRPC service.
/// <list type="bullet">
///   <item>
///     On a <b>concrete class</b>: opts the class into <c>MapWolverineGrpcServices()</c> discovery
///     when its name doesn't follow the <c>GrpcService</c> suffix convention. Also used on abstract
///     proto-first stubs to trigger Wolverine's code-generation pipeline.
///   </item>
///   <item>
///     On a <b><c>[ServiceContract]</c> interface</b>: activates Option C code-first codegen —
///     Wolverine generates a concrete implementation of the interface at startup, forwarding each
///     method to the Wolverine message bus. No hand-written service class is required.
///   </item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class WolverineGrpcServiceAttribute : Attribute;
