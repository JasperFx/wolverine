using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

/// <summary>
/// A frame that produces a chain's response after the commit, and can produce the same response before it instead.
/// </summary>
internal interface IHasBeforeCommitResponse
{
    /// <summary>
    /// Why the response cannot be produced before this chain's commit, or null when it can.
    /// </summary>
    string? WhyNotBeforeCommit(IChain chain, IServiceContainer container);

    /// <summary>
    /// New frames that produce the same response before the commit. Does not change the chain.
    /// </summary>
    (Frame[] Frames, Variable Response) BuildBeforeCommitResponse();
}
