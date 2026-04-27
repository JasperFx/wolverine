using JasperFx;
using JasperFx.CodeGeneration;

namespace Wolverine.Grpc;

/// <summary>
///     Apply your own conventions or structural modifications to Wolverine-managed gRPC chains
///     during bootstrapping. Analogous to <c>IHttpPolicy</c> for HTTP endpoints.
///     Register via <see cref="WolverineGrpcOptions.AddPolicy{T}()"/> or
///     <see cref="WolverineGrpcOptions.AddPolicy(IGrpcChainPolicy)"/>.
/// </summary>
/// <remarks>
///     Receives all three chain kinds as typed lists so implementations can target a specific
///     chain type without casting. Code-first chains (<see cref="CodeFirstGrpcServiceChain"/>)
///     are included for inspection even though they do not yet participate in the
///     <c>Chain&lt;&gt;</c> middleware pipeline (P3).
/// </remarks>
public interface IGrpcChainPolicy
{
    /// <summary>
    ///     Called during bootstrapping after all gRPC chains are discovered, immediately
    ///     before code generation runs.
    /// </summary>
    /// <param name="protoFirstChains">All proto-first gRPC chains (abstract stub → generated wrapper).</param>
    /// <param name="codeFirstChains">All code-first gRPC chains (<c>[ServiceContract]</c> interface → generated implementation).</param>
    /// <param name="handWrittenChains">All hand-written gRPC chains (concrete service class → generated delegation wrapper).</param>
    /// <param name="rules">The active code-generation rules.</param>
    /// <param name="container">The application's IoC container.</param>
    void Apply(
        IReadOnlyList<GrpcServiceChain> protoFirstChains,
        IReadOnlyList<CodeFirstGrpcServiceChain> codeFirstChains,
        IReadOnlyList<HandWrittenGrpcServiceChain> handWrittenChains,
        GenerationRules rules,
        IServiceContainer container);
}

internal sealed class LambdaGrpcChainPolicy : IGrpcChainPolicy
{
    private readonly Action<IReadOnlyList<GrpcServiceChain>, IReadOnlyList<CodeFirstGrpcServiceChain>,
        IReadOnlyList<HandWrittenGrpcServiceChain>, GenerationRules, IServiceContainer> _action;

    internal LambdaGrpcChainPolicy(
        Action<IReadOnlyList<GrpcServiceChain>, IReadOnlyList<CodeFirstGrpcServiceChain>,
            IReadOnlyList<HandWrittenGrpcServiceChain>, GenerationRules, IServiceContainer> action)
    {
        _action = action;
    }

    public void Apply(
        IReadOnlyList<GrpcServiceChain> protoFirstChains,
        IReadOnlyList<CodeFirstGrpcServiceChain> codeFirstChains,
        IReadOnlyList<HandWrittenGrpcServiceChain> handWrittenChains,
        GenerationRules rules,
        IServiceContainer container)
    {
        _action(protoFirstChains, codeFirstChains, handWrittenChains, rules, container);
    }
}
