using Fisher;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Persistence.Codegen;

namespace Wolverine.Fisher.Codegen;

/// <summary>
/// GH-4571. Shared rendering for the frames that make a logical deduplication claim ride the Fisher
/// session's transaction.
/// </summary>
internal static class FisherDeduplicationRendering
{
    public static string MarkerUsage(Type? ancillaryStoreMarker)
        => ancillaryStoreMarker == null ? "null" : $"typeof({ancillaryStoreMarker.FullNameInCode()})";
}

/// <summary>
/// GH-4571. Asks whether the logical deduplication id has already been claimed, THROUGH the Fisher
/// session — producing the same <c>bool</c> that <c>ClaimDeduplicationIdFrame</c> produces, so every chain
/// type's existing refusal works unchanged.
///
/// <para>
/// Not an <c>IBatchableFrame</c>, unlike the Marten twin: <c>Fisher.Batching.IBatchedQuery</c> has no
/// <c>AddItem&lt;T&gt;(IQueryHandler&lt;T&gt;)</c> equivalent to enlist a raw-SQL existence check into, so
/// the check pays its own round trip. That is a missed optimisation rather than a correctness gap — the
/// guarantee comes from the claim riding the transaction, not from where the check ran. On SQLite the
/// round trip is a local file read on a connection this session already holds.
/// </para>
/// </summary>
internal class FisherDeduplicationClaimExistsFrame : AsyncFrame
{
    private readonly Variable _deduplicationId;
    private readonly Type? _ancillaryStoreMarker;
    private Variable? _session;
    private Variable? _deduplicator;
    private Variable? _cancellation;

    public FisherDeduplicationClaimExistsFrame(Variable deduplicationId, Type? ancillaryStoreMarker)
    {
        _deduplicationId = deduplicationId;
        _ancillaryStoreMarker = ancillaryStoreMarker;

        // Deliberately the same variable name the non-transactional frame uses, so the generated refusal
        // reads identically on both paths.
        Variable = new Variable(typeof(bool), "isDuplicateMessage", this);
    }

    /// <summary><see langword="true" /> when this execution must be refused as a duplicate.</summary>
    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4571: has this logical deduplication id already been claimed?");

        // Guarded rather than unconditional: an optional id that is absent means "this message is not
        // deduplicated", and unkeyed traffic on a mixed stream is supposed to cost no round trip at all.
        writer.Write($"var {Variable.Usage} = false;");
        writer.Write($"BLOCK:if (!string.IsNullOrWhiteSpace({_deduplicationId.Usage}))");
        writer.Write(
            $"{Variable.Usage} = await {_deduplicator!.Usage}.{nameof(IFisherDeduplicator.HasClaimAsync)}({_session!.Usage}, {_deduplicationId.Usage}, {FisherDeduplicationRendering.MarkerUsage(_ancillaryStoreMarker)}, {_cancellation!.Usage}).ConfigureAwait(false);");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _deduplicator = chain.FindVariable(typeof(IFisherDeduplicator));
        yield return _deduplicator;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}

/// <summary>
/// GH-4571. Enlists the claim in the Fisher session's transaction, so it is written in the same
/// transaction as the handler's events and documents.
///
/// <para>
/// Nothing is written here. A chain that throws never reaches <c>SaveChangesAsync</c>; a chain that stops
/// with a <c>ProblemDetails</c> 404 or a FluentValidation 400 returns before it. Either way the id was
/// never claimed, so there is nothing to give back — which is what removes the compensating release, its
/// status-code test, and the round trip each of them cost.
/// </para>
/// </summary>
internal class QueueFisherDeduplicationClaimFrame : SyncFrame, IDeduplicationClaimFrame
{
    private readonly Variable _deduplicationId;
    private readonly Type? _ancillaryStoreMarker;
    private Variable? _session;
    private Variable? _deduplicator;

    /// <param name="isDuplicate">
    /// The check's result. Not read by the generated code — it is declared as a dependency purely so the
    /// arranger emits this frame AFTER the refusal, keeping the generated method readable. Correctness
    /// does not rest on it: a refusal returns before any commit, so a claim enlisted ahead of one would
    /// never be written either.
    /// </param>
    public QueueFisherDeduplicationClaimFrame(Variable deduplicationId, Variable isDuplicate,
        Type? ancillaryStoreMarker)
    {
        _deduplicationId = deduplicationId;
        _ancillaryStoreMarker = ancillaryStoreMarker;
        uses.Add(isDuplicate);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4571: claim the logical deduplication id inside this Fisher transaction");
        writer.Write($"BLOCK:if (!string.IsNullOrWhiteSpace({_deduplicationId.Usage}))");
        writer.Write(
            $"{_deduplicator!.Usage}.{nameof(IFisherDeduplicator.QueueClaim)}({_session!.Usage}, {_deduplicationId.Usage}, {FisherDeduplicationRendering.MarkerUsage(_ancillaryStoreMarker)});");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _deduplicator = chain.FindVariable(typeof(IFisherDeduplicator));
        yield return _deduplicator;
    }
}
