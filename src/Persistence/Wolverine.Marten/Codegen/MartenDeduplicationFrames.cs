using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Linq.QueryHandlers;
using Marten.Services.BatchQuerying;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Codegen;

namespace Wolverine.Marten.Codegen;

/// <summary>
/// GH-4505. Shared rendering for the three frames that make a logical deduplication claim ride the
/// Marten session's transaction.
/// </summary>
internal static class MartenDeduplicationRendering
{
    public static string MarkerUsage(Type? ancillaryStoreMarker)
        => ancillaryStoreMarker == null ? "null" : $"typeof({ancillaryStoreMarker.FullNameInCode()})";

    /// <summary>F#'s <c>typeof&lt;T&gt;</c> rather than C#'s <c>typeof(T)</c>.</summary>
    public static string FSharpMarkerUsage(Type? ancillaryStoreMarker)
        => ancillaryStoreMarker == null ? "null" : $"typeof<{ancillaryStoreMarker.FSharpName()}>";
}

/// <summary>
/// GH-4505. Asks whether the logical deduplication id has already been claimed, THROUGH the Marten
/// session — producing the same <c>bool</c> that <c>ClaimDeduplicationIdFrame</c> produces, so every
/// chain type's existing refusal works unchanged.
///
/// <para>
/// This is an <see cref="IBatchableFrame" />, so <see cref="MartenBatchingPolicy" /> sweeps it into
/// whatever <c>IBatchedQuery</c> the chain was already running. On the composition this issue is
/// about — <c>[Deduplicated]</c> on a <c>[WriteAggregate]</c> endpoint — the check and the
/// <c>FetchForWriting</c> land in ONE round trip, where the shipped claim-and-release pays two on the
/// happy path and three on the failure path.
/// </para>
///
/// <para>
/// A chain whose id is OPTIONAL opts out of the batch (<see cref="CanBatch" />). Batch enlistment is
/// unconditional code, and an unkeyed message on a mixed stream is supposed to cost no database round
/// trip at all — which only the guarded standalone form can promise.
/// </para>
/// </summary>
internal class MartenDeduplicationClaimExistsFrame : AsyncFrame, IBatchableFrame
{
    private readonly Variable _deduplicationId;
    private readonly Type? _ancillaryStoreMarker;
    private Variable? _session;
    private Variable? _deduplicator;
    private Variable? _cancellation;
    private Variable? _batchQuery;
    private Variable? _batchQueryItem;

    public MartenDeduplicationClaimExistsFrame(Variable deduplicationId, Type? ancillaryStoreMarker, bool canBatch)
    {
        _deduplicationId = deduplicationId;
        _ancillaryStoreMarker = ancillaryStoreMarker;
        CanBatch = canBatch;

        // Deliberately the same variable name the non-transactional frame uses, so the generated
        // refusal reads identically on both paths.
        Variable = new Variable(typeof(bool), "isDuplicateMessage", this);
    }

    /// <summary>
    /// <see langword="true" /> when this execution must be refused as a duplicate.
    /// </summary>
    public Variable Variable { get; }

    /// <summary>
    /// Whether <see cref="MartenBatchingPolicy" /> may fold this check into a shared batched query.
    /// False when the id is optional — see the class remarks.
    /// </summary>
    public bool CanBatch { get; }

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(
            $"var {_batchQueryItem!.Usage} = {_batchQuery!.Usage}.{nameof(IBatchedQuery.AddItem)}({_deduplicator!.Usage}.{nameof(IMartenDeduplicator.ClaimExistsQuery)}({_deduplicationId.Usage}, {MartenDeduplicationRendering.MarkerUsage(_ancillaryStoreMarker)}));");
    }

    public void EnlistInBatchQuery(Variable batchQuery)
    {
        _batchQueryItem = new Variable(typeof(Task<bool>), Variable.Usage + "_BatchItem", this);
        _batchQuery = batchQuery;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4505: has this logical deduplication id already been claimed?");

        if (_batchQueryItem != null)
        {
            writer.Write($"var {Variable.Usage} = await {_batchQueryItem.Usage}.ConfigureAwait(false);");
        }
        else
        {
            writer.Write($"var {Variable.Usage} = false;");
            writer.Write($"BLOCK:if (!string.IsNullOrWhiteSpace({_deduplicationId.Usage}))");
            writer.Write(
                $"{Variable.Usage} = await {_deduplicator!.Usage}.{nameof(IMartenDeduplicator.HasClaimAsync)}({_session!.Usage}, {_deduplicationId.Usage}, {MartenDeduplicationRendering.MarkerUsage(_ancillaryStoreMarker)}, {_cancellation!.Usage}).ConfigureAwait(false);");
            writer.FinishBlock();
        }

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _deduplicator = chain.FindVariable(typeof(IMartenDeduplicator));
        yield return _deduplicator;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        if (_batchQuery != null)
        {
            yield return _batchQuery;
        }
    }
}

/// <summary>
/// GH-4505. Queues the claim onto the Marten session's unit of work, so it is written in the same
/// transaction as the handler's events and documents.
///
/// <para>
/// Nothing is written here. A chain that throws never reaches <c>SaveChangesAsync</c>; a chain that
/// stops with a <c>ProblemDetails</c> 404 or a FluentValidation 400 returns before it. Either way the
/// id was never claimed, so there is nothing to give back — which is what removes the compensating
/// release, its status-code test, and the round trip each of them cost.
/// </para>
/// </summary>
internal class QueueMartenDeduplicationClaimFrame : SyncFrame, IDeduplicationClaimFrame
{
    private readonly Variable _deduplicationId;
    private readonly Type? _ancillaryStoreMarker;
    private readonly TimeSpan? _window;
    private Variable? _session;
    private Variable? _deduplicator;

    /// <param name="isDuplicate">
    /// The check's result. Not read by the generated code — it is declared as a dependency purely so the
    /// arranger emits this frame AFTER the refusal, keeping the generated method readable. Correctness
    /// does not rest on it: a refusal returns before any commit, so a claim queued ahead of one would
    /// never be written either.
    /// </param>
    /// <param name="window">
    /// The chain's <see cref="Wolverine.Persistence.DeduplicationRequirement.Window" />, or null for the host-wide window.
    /// </param>
    public QueueMartenDeduplicationClaimFrame(Variable deduplicationId, Variable isDuplicate,
        Type? ancillaryStoreMarker, TimeSpan? window)
    {
        _deduplicationId = deduplicationId;
        _ancillaryStoreMarker = ancillaryStoreMarker;
        _window = window;
        uses.Add(isDuplicate);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4505: claim the logical deduplication id inside this Marten transaction");
        writer.Write($"BLOCK:if (!string.IsNullOrWhiteSpace({_deduplicationId.Usage}))");
        writer.Write(
            $"{_deduplicator!.Usage}.{nameof(IMartenDeduplicator.QueueClaim)}({_session!.Usage}, {_deduplicationId.Usage}, {DeduplicationWindowRendering.ArgumentFor(_window)}{MartenDeduplicationRendering.MarkerUsage(_ancillaryStoreMarker)});");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _deduplicator = chain.FindVariable(typeof(IMartenDeduplicator));
        yield return _deduplicator;
    }
}

/// <summary>
/// GH-5160. Queues a <c>[DeduplicatedWithResponse]</c> claim, with its response, onto the Marten session's unit of work.
/// Placed just before <c>SaveChangesAsync</c>, after the endpoint has set the status and <c>Location</c>.
///
/// <para>
/// Nothing is written here, as for <see cref="QueueMartenDeduplicationClaimFrame" />: a chain that throws or
/// stops never commits, so it never claimed.
/// </para>
/// </summary>
internal class QueueMartenDeduplicatedResponseClaimFrame : SyncFrame
{
    private readonly Variable _claim;
    private readonly Type? _ancillaryStoreMarker;
    private Variable? _session;
    private Variable? _responses;

    public QueueMartenDeduplicatedResponseClaimFrame(Variable claim, Type? ancillaryStoreMarker)
    {
        _claim = claim;
        _ancillaryStoreMarker = ancillaryStoreMarker;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5160: claim the key, with its response, inside this Marten transaction");
        writer.Write(
            $"{_responses!.Usage}.{nameof(IMartenDeduplicatedResponses.QueueClaim)}({_session!.Usage}, {_claim.Usage}, {MartenDeduplicationRendering.MarkerUsage(_ancillaryStoreMarker)});");

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5160: claim the key, with its response, inside this Marten transaction");
        writer.Write(
            $"{_responses!.FSharpUsage}.{nameof(IMartenDeduplicatedResponses.QueueClaim)}({_session!.FSharpUsage}, {_claim.FSharpUsage}, {MartenDeduplicationRendering.FSharpMarkerUsage(_ancillaryStoreMarker)})");

        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _claim;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _responses = chain.FindVariable(typeof(IMartenDeduplicatedResponses));
        yield return _responses;
    }
}

/// <summary>
/// GH-5160. Reads an earlier request's committed <c>[DeduplicatedWithResponse]</c> claim THROUGH the Marten session,
/// producing the claim a repeat is answered from. The twin of <see cref="MartenDeduplicationClaimExistsFrame" />, and
/// batchable on the same terms: <see cref="MartenBatchingPolicy" /> sweeps it into the chain's batched query, so with
/// <c>[WriteAggregate]</c> the lookup and <c>FetchForWriting</c> share one round trip. A chain whose key is optional
/// opts out (<see cref="CanBatch" />), so a request without one costs no round trip at all.
/// </summary>
internal class MartenDeduplicatedResponseClaimFrame : AsyncFrame, IBatchableFrame
{
    private readonly Variable _deduplicationId;
    private readonly Type? _ancillaryStoreMarker;
    private Variable? _session;
    private Variable? _responses;
    private Variable? _cancellation;
    private Variable? _batchQuery;
    private Variable? _batchQueryItem;

    /// <param name="deduplicationId">Already hashed; null at runtime for a request without a key.</param>
    public MartenDeduplicatedResponseClaimFrame(Variable deduplicationId, Type? ancillaryStoreMarker, bool canBatch)
    {
        _deduplicationId = deduplicationId;
        _ancillaryStoreMarker = ancillaryStoreMarker;
        CanBatch = canBatch;
        Variable = new Variable(typeof(DeduplicatedResponseClaim), "deduplicatedResponseClaim", this);
    }

    /// <summary>The claim an earlier request committed, or null.</summary>
    public Variable Variable { get; }

    /// <summary>
    /// Whether <see cref="MartenBatchingPolicy" /> may fold this lookup into a shared batched query. False when the key
    /// is optional.
    /// </summary>
    public bool CanBatch { get; }

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(
            $"var {_batchQueryItem!.Usage} = {_batchQuery!.Usage}.{nameof(IBatchedQuery.AddItem)}({_responses!.Usage}.{nameof(IMartenDeduplicatedResponses.ClaimQuery)}({_session!.Usage}, {_deduplicationId.Usage}, {MartenDeduplicationRendering.MarkerUsage(_ancillaryStoreMarker)}));");
    }

    public void EnlistInBatchQuery(Variable batchQuery)
    {
        _batchQueryItem = new Variable(typeof(Task<DeduplicatedResponseClaim>), Variable.Usage + "_BatchItem", this);
        _batchQuery = batchQuery;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5160: has an earlier request already committed this key, and with what?");

        if (_batchQueryItem != null)
        {
            writer.Write($"var {Variable.Usage} = await {_batchQueryItem.Usage}.ConfigureAwait(false);");
        }
        else
        {
            writer.Write($"{typeof(DeduplicatedResponseClaim).FullNameInCode()}? {Variable.Usage} = null;");
            writer.Write($"BLOCK:if (!string.IsNullOrWhiteSpace({_deduplicationId.Usage}))");
            writer.Write(
                $"{Variable.Usage} = await {_responses!.Usage}.{nameof(IMartenDeduplicatedResponses.FindClaimAsync)}({_session!.Usage}, {_deduplicationId.Usage}, {MartenDeduplicationRendering.MarkerUsage(_ancillaryStoreMarker)}, {_cancellation!.Usage}).ConfigureAwait(false);");
            writer.FinishBlock();
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5160: has an earlier request already committed this key, and with what?");

        // Batch enlistment has no F# emitter (WriteCodeToEnlistInBatchQuery is C#), so the F# form is always the
        // standalone lookup, guarded as in C#
        writer.Write(
            $"let! {Variable.Usage} = if System.String.IsNullOrWhiteSpace({_deduplicationId.FSharpUsage}) then System.Threading.Tasks.Task.FromResult<{typeof(DeduplicatedResponseClaim).FSharpName()}>(null) else {_responses!.FSharpUsage}.{nameof(IMartenDeduplicatedResponses.FindClaimAsync)}({_session!.FSharpUsage}, {_deduplicationId.FSharpUsage}, {MartenDeduplicationRendering.FSharpMarkerUsage(_ancillaryStoreMarker)}, {_cancellation!.FSharpUsage})");

        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _responses = chain.FindVariable(typeof(IMartenDeduplicatedResponses));
        yield return _responses;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        if (_batchQuery != null)
        {
            yield return _batchQuery;
        }
    }
}
