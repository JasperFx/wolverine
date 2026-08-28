using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Codegen;

/// <summary>
/// GH-4180. Tests whether a required logical deduplication id is absent, producing the
/// <c>bool</c> that the chain-specific stop condition branches on.
///
/// <para>
/// Emitted only when <see cref="DeduplicationRequirement.Required" /> is set. When it is not, a
/// missing id simply means "this message is not deduplicated", and no test is generated at all.
/// </para>
/// </summary>
internal class DeduplicationIdMissingFrame : SyncFrame
{
    private readonly Variable _deduplicationId;

    public DeduplicationIdMissingFrame(Variable deduplicationId)
    {
        _deduplicationId = deduplicationId;
        Variable = new Variable(typeof(bool), "missingDeduplicationId", this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"var {Variable.Usage} = string.IsNullOrWhiteSpace({_deduplicationId.Usage});");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;
    }
}

/// <summary>
/// GH-4180. Claims the logical deduplication id, producing the <c>bool</c> that says whether this
/// execution is a duplicate.
///
/// <para>
/// The claim is an INSERT that either succeeds or trips a unique constraint — never a SELECT
/// followed by an INSERT. That distinction is the whole feature: the motivating duplicates are
/// concurrent (an operator double-click, two nodes replaying the same schedule), and a
/// check-then-act would let both through while passing every single-threaded test written against
/// it. See <see cref="Durability.IDeduplicationStore.TryClaimAsync" />.
/// </para>
///
/// <para>
/// When the id is optional and absent, the claim is skipped and execution continues — the emitted
/// guard is <c>if (!string.IsNullOrWhiteSpace(id))</c> rather than an unconditional call, so
/// unkeyed traffic on a mixed stream costs no database round trip at all.
/// </para>
/// </summary>
internal class ClaimDeduplicationIdFrame : AsyncFrame
{
    private readonly Variable _deduplicationId;
    private readonly Type? _ancillaryStoreMarker;
    private readonly string? _deduplicatorUsage;
    private readonly string? _cancellationUsage;
    private Variable? _deduplicator;
    private Variable? _cancellation;

    /// <param name="deduplicatorUsage">
    /// Explicit expression for the <see cref="IMessageDeduplicator" />, or null to resolve it as an
    /// ordinary chain variable. Wolverine's gRPC codegen supplies one: it composes generated service
    /// types out of explicit <c>InjectedField</c>s rather than the container-driven variable sources
    /// that handler and HTTP chains use, so a <c>FindVariable</c> there would come up empty.
    /// </param>
    /// <param name="cancellationUsage">
    /// Explicit expression for the cancellation token, or null to resolve it as a chain variable.
    /// gRPC supplies <c>context.CancellationToken</c> for the same reason.
    /// </param>
    public ClaimDeduplicationIdFrame(Variable deduplicationId, Type? ancillaryStoreMarker,
        string? deduplicatorUsage = null, string? cancellationUsage = null)
    {
        _deduplicationId = deduplicationId;
        _ancillaryStoreMarker = ancillaryStoreMarker;
        _deduplicatorUsage = deduplicatorUsage;
        _cancellationUsage = cancellationUsage;
        Variable = new Variable(typeof(bool), "isDuplicateMessage", this);
    }

    private string deduplicatorUsage => _deduplicatorUsage ?? _deduplicator!.Usage;
    private string cancellationUsage => _cancellationUsage ?? _cancellation!.Usage;

    /// <summary>
    /// <see langword="true" /> when this execution must be refused as a duplicate. Deliberately phrased
    /// as "is duplicate" rather than "was claimed" so the generated stop condition reads
    /// <c>if (isDuplicateMessage)</c>, matching every other stop condition in the codebase.
    /// </summary>
    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var marker = _ancillaryStoreMarker == null
            ? "null"
            : $"typeof({_ancillaryStoreMarker.FullNameInCode()})";

        writer.Write($"var {Variable.Usage} = false;");
        writer.Write($"BLOCK:if (!string.IsNullOrWhiteSpace({_deduplicationId.Usage}))");
        writer.Write(
            $"{Variable.Usage} = !(await {deduplicatorUsage}.{nameof(IMessageDeduplicator.TryClaimAsync)}({_deduplicationId.Usage}, {marker}, {cancellationUsage}).ConfigureAwait(false));");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

        if (_deduplicatorUsage == null)
        {
            _deduplicator = chain.FindVariable(typeof(IMessageDeduplicator));
            yield return _deduplicator;
        }

        if (_cancellationUsage == null)
        {
            _cancellation = chain.FindVariable(typeof(CancellationToken));
            yield return _cancellation;
        }
    }
}

/// <summary>
/// GH-4180. Releases the logical deduplication claim when execution failed, so that a retry is not
/// discarded as a duplicate of its own failed attempt.
///
/// <para>
/// Emitted ONLY into non-transactional chains. When the chain is transactional the claim is written
/// inside the same transaction as the handler's work, so a rollback removes it and a compensating
/// release would be both redundant and wrong — it would delete a claim that no longer exists, or
/// worse, one that a concurrent caller has since legitimately taken.
/// </para>
///
/// <para>
/// Without this, the first failed attempt permanently poisons the id: every retry is refused as a
/// duplicate, the work is silently never done, and the logs show a successful deduplication rather
/// than a lost message. That is a strictly worse outcome than not having the feature.
/// </para>
/// </summary>
internal class ReleaseDeduplicationIdOnFailureFrame : AsyncFrame
{
    private readonly Variable _deduplicationId;
    private readonly Type? _ancillaryStoreMarker;
    private readonly string? _deduplicatorUsage;
    private readonly string? _cancellationUsage;
    private Variable? _deduplicator;
    private Variable? _cancellation;

    /// <inheritdoc cref="ClaimDeduplicationIdFrame(Variable, Type, string, string)" />
    public ReleaseDeduplicationIdOnFailureFrame(Variable deduplicationId, Type? ancillaryStoreMarker,
        string? deduplicatorUsage = null, string? cancellationUsage = null)
    {
        _deduplicationId = deduplicationId;
        _ancillaryStoreMarker = ancillaryStoreMarker;
        _deduplicatorUsage = deduplicatorUsage;
        _cancellationUsage = cancellationUsage;
    }

    private string deduplicatorUsage => _deduplicatorUsage ?? _deduplicator!.Usage;
    private string cancellationUsage => _cancellationUsage ?? _cancellation!.Usage;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var marker = _ancillaryStoreMarker == null
            ? "null"
            : $"typeof({_ancillaryStoreMarker.FullNameInCode()})";

        // Wraps everything downstream, then rethrows. `throw;` rather than `throw e;` so the original
        // stack trace survives to the error policies -- this frame compensates for a failure, it does
        // not handle one, and swallowing here would turn every handler exception into a silent success.
        writer.Write("BLOCK:try");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();

        writer.Write("BLOCK:catch");
        writer.Write($"BLOCK:if (!string.IsNullOrWhiteSpace({_deduplicationId.Usage}))");
        writer.Write(
            $"await {deduplicatorUsage}.{nameof(IMessageDeduplicator.ReleaseAsync)}({_deduplicationId.Usage}, {marker}, {cancellationUsage}).ConfigureAwait(false);");
        writer.FinishBlock();
        writer.Write("throw;");
        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

        if (_deduplicatorUsage == null)
        {
            _deduplicator = chain.FindVariable(typeof(IMessageDeduplicator));
            yield return _deduplicator;
        }

        if (_cancellationUsage == null)
        {
            _cancellation = chain.FindVariable(typeof(CancellationToken));
            yield return _cancellation;
        }
    }
}
