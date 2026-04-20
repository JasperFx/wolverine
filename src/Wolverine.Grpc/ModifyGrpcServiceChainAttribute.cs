using JasperFx.CodeGeneration;
using Wolverine.Configuration;

namespace Wolverine.Grpc;

/// <summary>
///     Base class for attributes that configure how a proto-first gRPC service chain is handled
///     by applying middleware, error handling rules, or customizing the generated wrapper.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public abstract class ModifyGrpcServiceChainAttribute : Attribute, IModifyChain<GrpcServiceChain>
{
    /// <summary>
    ///     Called by Wolverine during bootstrapping before the gRPC service wrapper is generated and compiled.
    /// </summary>
    public abstract void Modify(GrpcServiceChain chain, GenerationRules rules);
}
