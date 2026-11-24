using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Configuration;
using Wolverine.Fisher.Codegen;
using Wolverine.Persistence;

namespace Wolverine.Fisher.Persistence.Sagas;

/// <summary>
/// GH-5835. The Fisher side of <see cref="IDatabaseCommitHookProvider" />: hear that this chain's session committed,
/// through the same <see cref="DeduplicationClaimCommitted" /> transaction participant that GH-5613 enlists with the
/// claim. Wolverine.Http uses it so an endpoint that commits its own session, claim included, and then fails is answered
/// with its own failure rather than as a repeat of a concurrent request; and so a claim-and-release
/// <c>[DeduplicatedWithResponse]</c> keeps its claim over work that committed before the commit call threw (GH-5165).
/// </summary>
internal partial class FisherPersistenceFrameProvider : IDatabaseCommitHookProvider
{
    public bool TryBuildDatabaseCommitHook(IChain chain, MethodInfo onCommitted, [NotNullWhen(true)] out Frame? hook)
    {
        hook = null;

        if (!chain.Middleware.OfType<CreateDocumentSessionFrame>().Any()) return false;
        if (!chain.Postprocessors.OfType<DocumentSessionSaveChanges>().Any()) return false;

        hook = new EnlistFisherDatabaseCommitHookFrame(onCommitted);
        return true;
    }
}
