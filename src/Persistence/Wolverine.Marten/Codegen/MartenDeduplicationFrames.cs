using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Linq.QueryHandlers;
using Marten.Services.BatchQuerying;
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
    private Variable? _session;
    private Variable? _deduplicator;

    /// <param name="isDuplicate">
    /// The check's result. Not read by the generated code — it is declared as a dependency purely so the
    /// arranger emits this frame AFTER the refusal, keeping the generated method readable. Correctness
    /// does not rest on it: a refusal returns before any commit, so a claim queued ahead of one would
    /// never be written either.
    /// </param>
    public QueueMartenDeduplicationClaimFrame(Variable deduplicationId, Variable isDuplicate,
        Type? ancillaryStoreMarker)
    {
        _deduplicationId = deduplicationId;
        _ancillaryStoreMarker = ancillaryStoreMarker;
        uses.Add(isDuplicate);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4505: claim the logical deduplication id inside this Marten transaction");
        writer.Write($"BLOCK:if (!string.IsNullOrWhiteSpace({_deduplicationId.Usage}))");
        writer.Write(
            $"{_deduplicator!.Usage}.{nameof(IMartenDeduplicator.QueueClaim)}({_session!.Usage}, {_deduplicationId.Usage}, {MartenDeduplicationRendering.MarkerUsage(_ancillaryStoreMarker)});");
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
/// GH-4505. Turns the one duplicate the optimistic check cannot catch — two concurrent callers, both
/// told "not claimed", both queueing the INSERT — into the same refusal the check produces.
///
/// <para>
/// Without this the loser of that race escapes as a raw Postgres unique-violation: a 500 on an HTTP
/// endpoint, a dead-letter on a handler. With it, the caller gets the endpoint's configured 409 (or
/// the handler discards) exactly as if the check had won. That is the price of the optimistic pattern
/// and it is worth saying plainly: the loser does its work and throws it away at commit, where the
/// shipped claim-and-release refuses it before doing any.
/// </para>
///
/// <para>
/// The refusal is written AFTER the try block, not inside the catch, and that ordering is load-bearing
/// on HTTP: <c>SaveChangesAsync</c> is a postprocessor and the response writer is appended after it, so
/// the commit fails while <c>Response.HasStarted</c> is still false and a clean problem document can
/// still go out.
/// </para>
/// </summary>
internal class RefuseDuplicateDeduplicationClaimFrame : Frame
{
    private readonly Frame[] _refusal;

    public RefuseDuplicateDeduplicationClaimFrame(Func<Variable, Frame[]> buildRefusal) : base(true)
    {
        LostTheRace = new Variable(typeof(bool), "duplicateClaimLostTheRace", this);
        _refusal = buildRefusal(LostTheRace);
    }

    /// <summary>Set when the commit tripped the deduplication table's primary key.</summary>
    public Variable LostTheRace { get; }

    /// <summary>
    /// Name of the caught exception. Deliberately not <c>e</c>: this frame nests inside whatever the rest
    /// of the chain generates, and a one-letter name is the one most likely to collide with a handler
    /// parameter or another frame's variable.
    /// </summary>
    private const string ExceptionName = "duplicateClaimException";

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // Nothing to convert the failure INTO means the catch would swallow a real error and report the
        // work as done. Generate no wrapper at all rather than that. Unreachable for every chain type
        // Wolverine ships -- the base BuildDeduplicationStopCondition throws rather than answering empty --
        // but the failure mode is silent enough to be worth refusing structurally.
        if (_refusal.Length == 0)
        {
            Next?.GenerateCode(method, writer);
            return;
        }

        writer.Write($"var {LostTheRace.Usage} = false;");
        writer.Write("BLOCK:try");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();

        // An exception filter rather than a test inside the catch body, so an unrelated failure is never
        // caught and rethrown -- a rethrow from here would push the original stack trace one frame deeper
        // for every exception the error policies are supposed to see unaltered.
        writer.Write(
            $"BLOCK:catch ({typeof(Exception).FullNameInCode()} {ExceptionName}) when ({typeof(MartenDeduplicationFailures).FullNameInCode()}.{nameof(MartenDeduplicationFailures.IsDuplicateDeduplicationClaim)}({ExceptionName}))");
        writer.WriteComment("GH-4505: a concurrent caller committed this logical id first");
        writer.Write($"{LostTheRace.Usage} = true;");
        writer.FinishBlock();

        for (var i = 0; i < _refusal.Length - 1; i++)
        {
            _refusal[i].Next = _refusal[i + 1];
        }

        _refusal[^1].Next = null;
        _refusal[0].GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var frame in _refusal)
        {
            foreach (var variable in frame.FindVariables(chain))
            {
                yield return variable;
            }
        }
    }
}
