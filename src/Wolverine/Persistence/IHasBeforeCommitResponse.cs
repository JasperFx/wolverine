using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

internal interface IHasBeforeCommitResponse
{
    /// <summary>
    /// Builds a new frame that produces the same response before the commit, or returns false when this chain's store
    /// can't. Does not change the chain.
    /// </summary>
    bool TryBuildBeforeCommitResponse(IChain chain, IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame, [NotNullWhen(true)] out Variable? response);
}
