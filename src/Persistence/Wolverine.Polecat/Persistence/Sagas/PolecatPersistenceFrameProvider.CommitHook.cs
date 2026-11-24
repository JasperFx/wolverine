using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Configuration;
using Wolverine.Polecat.Codegen;
using Wolverine.Persistence;

namespace Wolverine.Polecat.Persistence.Sagas;

/// <summary>
/// GH-5835. The Polecat side of <see cref="IDatabaseCommitHookProvider" />: hear that this chain's session committed,
/// through the same <see cref="DeduplicationClaimCommitted" /> transaction participant that GH-5613 enlists with the
/// claim. Wolverine.Http uses it so an endpoint that commits its own session, claim included, and then fails is answered
/// with its own failure rather than as a repeat of a concurrent request; and so a claim-and-release
/// <c>[DeduplicatedWithResponse]</c> keeps its claim over work that committed before the commit call threw (GH-5165).
/// </summary>
internal partial class PolecatPersistenceFrameProvider : IDatabaseCommitHookProvider
{
    public bool TryBuildDatabaseCommitHook(IChain chain, MethodInfo onCommitted, [NotNullWhen(true)] out Frame? hook)
    {
        hook = null;

        if (!chain.Middleware.OfType<CreateDocumentSessionFrame>().Any()) return false;
        if (!chain.Postprocessors.OfType<DocumentSessionSaveChanges>().Any()) return false;

        hook = new EnlistPolecatDatabaseCommitHookFrame(onCommitted);
        return true;
    }
}
