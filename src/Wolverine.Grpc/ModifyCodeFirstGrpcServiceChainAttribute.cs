using JasperFx.CodeGeneration;
using Wolverine.Configuration;

namespace Wolverine.Grpc;

/// <summary>
///     Apply to a <c>[ServiceContract]</c> interface that carries <see cref="WolverineGrpcServiceAttribute"/>
///     to customize how Wolverine configures the generated code-first gRPC service implementation.
///     Mirrors the role of <see cref="ModifyGrpcServiceChainAttribute"/> for proto-first services.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, AllowMultiple = true)]
public abstract class ModifyCodeFirstGrpcServiceChainAttribute : Attribute, IModifyChain<CodeFirstGrpcServiceChain>
{
    public abstract void Modify(CodeFirstGrpcServiceChain chain, GenerationRules rules);
}
