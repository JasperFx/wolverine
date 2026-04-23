using JasperFx.CodeGeneration;
using Wolverine.Configuration;

namespace Wolverine.Grpc;

/// <summary>
///     Base attribute for applying modifications to a <see cref="HandWrittenGrpcServiceChain"/>.
///     Apply to a hand-written code-first gRPC service class or one of its methods to
///     customise the generated delegation wrapper before compilation.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public abstract class ModifyHandWrittenGrpcServiceChainAttribute : Attribute, IModifyChain<HandWrittenGrpcServiceChain>
{
    public abstract void Modify(HandWrittenGrpcServiceChain chain, GenerationRules rules);
}
