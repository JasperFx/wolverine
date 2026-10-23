using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

/// <summary>
/// GH-5165. A persistence provider whose commit call can still fail AFTER the database has committed, and that can say
/// when the database commit itself succeeded.
///
/// <para>
/// On Marten, <c>SaveChangesAsync</c> runs the session listeners' <c>AfterCommitAsync</c> after the database commit,
/// Wolverine's own outbox flush among them. A failure there leaves the work committed while the commit call throws, and
/// a <c>[DeduplicatedWithResponse]</c> claim taken by claim-and-release would then be released, so the client's retry
/// ran the work again. With this hook the chain learns the commit succeeded and keeps the claim.
/// </para>
///
/// <para>
/// A sibling of <see cref="IPersistenceFrameProvider" />, as <see cref="ITransactionalReplayableDeduplicationProvider" />
/// is, and internal for the same reason: only Wolverine.Marten implements it and only Wolverine.Http calls it. A
/// provider that commits and then flushes in separate steps, as EF Core does, does not need it.
/// </para>
/// </summary>
internal interface IDatabaseCommitHookProvider
{
    /// <summary>
    /// A frame, to place in the chain's middleware, that calls <paramref name="onCommitted" /> as soon as the chain's
    /// database commit succeeds and before anything else that runs inside the commit call. Or
    /// <see langword="false" /> when the chain does not commit through this provider.
    /// </summary>
    /// <param name="chain">The chain being woven.</param>
    /// <param name="onCommitted">
    /// A public static method with one parameter, resolved as a variable of the chain, such as the
    /// <c>HttpContext</c>.
    /// </param>
    /// <param name="hook">The frame.</param>
    bool TryBuildDatabaseCommitHook(IChain chain, MethodInfo onCommitted, [NotNullWhen(true)] out Frame? hook);
}
