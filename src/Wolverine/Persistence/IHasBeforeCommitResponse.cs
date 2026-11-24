using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

/// <summary>
/// GH-5721. A frame that produces a chain's response after the commit, and can produce the same response before it
/// instead: <c>UpdatedAggregate</c>'s <c>FetchLatest</c>, which can be <c>ProjectLatestForCommit</c> before the commit.
/// A <c>[DeduplicatedWithResponse]</c> endpoint that claims in its transaction has to have its response before the
/// commit, to store it with the claim.
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
