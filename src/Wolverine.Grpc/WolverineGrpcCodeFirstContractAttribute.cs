using System.Diagnostics.CodeAnalysis;

namespace Wolverine.Grpc;

/// <summary>
///     Assembly-level registration of a code-first gRPC contract for Wolverine's generated-implementation
///     path (GH-4396). The declarative twin of
///     <see cref="WolverineGrpcOptions.IncludeCodeFirstContract{T}"/>: put it in the host assembly
///     (or any assembly Wolverine scans) and name a <c>[ServiceContract]</c> interface that carries no
///     <see cref="WolverineGrpcServiceAttribute"/>, typically because it lives in a contracts assembly
///     that must not reference WolverineFx.Grpc.
///     <code>
///     [assembly: WolverineGrpcCodeFirstContract(typeof(IGreeterCodeFirstService))]
///     // or
///     [assembly: WolverineGrpcCodeFirstContract&lt;IGreeterCodeFirstService&gt;]
///     </code>
///     Read from the assemblies Wolverine already scans for handlers (<c>WolverineOptions.Assemblies</c>),
///     so an attribute placed outside the application assembly needs
///     <c>opts.Discovery.IncludeAssembly(...)</c>, the same rule an attributed interface follows. The named
///     type is validated when discovery reads it. Mirrors <c>[assembly: WolverineModule(typeof(T))]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public class WolverineGrpcCodeFirstContractAttribute : Attribute
{
    /// <param name="contractType">A non-generic interface carrying <c>[ServiceContract]</c>.</param>
    public WolverineGrpcCodeFirstContractAttribute(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type contractType)
    {
        ContractType = contractType ?? throw new ArgumentNullException(nameof(contractType));
    }

    /// <summary>The contract interface Wolverine should generate and map an implementation for.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
    public Type ContractType { get; }
}

/// <summary>
///     Generic form of <see cref="WolverineGrpcCodeFirstContractAttribute"/>:
///     <c>[assembly: WolverineGrpcCodeFirstContract&lt;IGreeterCodeFirstService&gt;]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public class WolverineGrpcCodeFirstContractAttribute<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>
    : WolverineGrpcCodeFirstContractAttribute
    where T : class
{
    public WolverineGrpcCodeFirstContractAttribute() : base(typeof(T))
    {
    }
}
