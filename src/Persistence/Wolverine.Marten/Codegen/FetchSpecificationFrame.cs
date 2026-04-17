using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Linq;
using Marten.Services.BatchQuerying;

namespace Wolverine.Marten.Codegen;

/// <summary>
/// Codegen frame that executes a Marten query "specification" — either an
/// <see cref="ICompiledQuery{TDoc,TOut}"/> or an <see cref="IQueryPlan{T}"/> —
/// and produces the query's result as a new variable for downstream frames
/// (Handle, Validate, After) to consume.
///
/// <para>
/// Detects at construction time whether the spec supports batching
/// (<see cref="IBatchQueryPlan{T}"/> for plans, always yes for compiled
/// queries) and routes execution through <see cref="MartenBatchingPolicy"/>
/// when multiple batchable operations are present in the same method.
/// </para>
/// </summary>
internal class FetchSpecificationFrame : AsyncFrame, IBatchableFrame
{
    private readonly Variable _spec;
    private readonly SpecKind _kind;
    private readonly Type? _docType;
    private readonly Type _resultType;

    private Variable? _session;
    private Variable? _cancellation;
    private Variable? _batchQuery;
    private Variable? _batchItem;

    public FetchSpecificationFrame(Variable specVar)
    {
        _spec = specVar ?? throw new ArgumentNullException(nameof(specVar));

        var specType = specVar.VariableType;

        var compiled = specType.FindInterfaceThatCloses(typeof(ICompiledQuery<,>));
        var batchPlan = specType.FindInterfaceThatCloses(typeof(IBatchQueryPlan<>));
        var queryPlan = specType.FindInterfaceThatCloses(typeof(IQueryPlan<>));

        if (compiled != null)
        {
            _kind = SpecKind.Compiled;
            var args = compiled.GetGenericArguments();
            _docType = args[0];
            _resultType = args[1];
            CanBatch = true;
        }
        else if (batchPlan != null)
        {
            _kind = SpecKind.Plan;
            _resultType = batchPlan.GetGenericArguments()[0];
            CanBatch = true;
            IsBatchOnlyPlan = queryPlan is null;
        }
        else if (queryPlan != null)
        {
            _kind = SpecKind.Plan;
            _resultType = queryPlan.GetGenericArguments()[0];
            // batchable only if spec ALSO implements IBatchQueryPlan<T>
            CanBatch = false;
        }
        else
        {
            throw new ArgumentException(
                $"Type {specType.FullName} does not implement ICompiledQuery<,>, IQueryPlan<>, or IBatchQueryPlan<>",
                nameof(specVar));
        }

        // Result variable — becomes available to downstream frames by type
        // Name derived from the spec var's usage name so two fetches for the same result
        // type don't collide (which can happen if the handler uses multiple compiled
        // queries that all produce the same T).
        var resultName = $"{specVar.Usage}_result";
        Result = new Variable(_resultType, resultName, this);
    }

    /// <summary>
    /// True if this spec can participate in a Marten <see cref="IBatchedQuery"/>
    /// (all compiled queries, and plans that implement <see cref="IBatchQueryPlan{T}"/>).
    /// </summary>
    public bool CanBatch { get; }

    /// <summary>
    /// True for plans that implement <see cref="IBatchQueryPlan{T}"/> but NOT
    /// the single-execution <see cref="IQueryPlan{T}"/> — these must always run
    /// inside a batch (they have no standalone execution path).
    /// </summary>
    public bool IsBatchOnlyPlan { get; }

    /// <summary>
    /// Variable produced by this frame, typed as the spec's result type.
    /// Downstream frames (Handle / Validate / After) bind to it by type.
    /// </summary>
    public Variable Result { get; }

    // ── IBatchableFrame ───────────────────────────────────────────────

    public void EnlistInBatchQuery(Variable batchQuery)
    {
        _batchQuery = batchQuery;
        _batchItem = new Variable(
            typeof(Task<>).MakeGenericType(_resultType),
            $"{_spec.Usage}_BatchItem",
            this);
    }

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchItem == null || _batchQuery == null) return;

        if (_kind == SpecKind.Compiled)
        {
            writer.WriteLine(
                $"var {_batchItem.Usage} = {_batchQuery.Usage}.{nameof(IBatchedQuery.Query)}" +
                $"<{_docType!.FullNameInCode()}, {_resultType.FullNameInCode()}>({_spec.Usage});");
        }
        else
        {
            // Plan path — always uses IBatchQueryPlan<T>. Frame is only
            // enlisted if CanBatch is true, which means either the plan
            // type implements IBatchQueryPlan<T> directly, or (for plans
            // that implement only IBatchQueryPlan<T>) it's batch-only.
            var batchPlanType = typeof(IBatchQueryPlan<>).MakeGenericType(_resultType).FullNameInCode();
            writer.WriteLine(
                $"var {_batchItem.Usage} = {_batchQuery.Usage}.{nameof(IBatchedQuery.QueryByPlan)}" +
                $"<{_resultType.FullNameInCode()}>(({batchPlanType}){_spec.Usage});");
        }
    }

    // ── Frame ─────────────────────────────────────────────────────────

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchItem != null)
        {
            // Batched path — the Query/QueryByPlan call was already emitted inside
            // MartenBatchFrame. Here we resolve the pending Task<T> into the result
            // variable that downstream frames consume.
            writer.WriteLine($"var {Result.Usage} = await {_batchItem.Usage}.ConfigureAwait(false);");
        }
        else
        {
            // Standalone path — emit direct session call
            if (_kind == SpecKind.Compiled)
            {
                writer.WriteLine(
                    $"var {Result.Usage} = await {_session!.Usage}.{nameof(IQuerySession.QueryAsync)}" +
                    $"<{_docType!.FullNameInCode()}, {_resultType.FullNameInCode()}>" +
                    $"({_spec.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
            }
            else
            {
                writer.WriteLine(
                    $"var {Result.Usage} = await {_session!.Usage}.{nameof(IQuerySession.QueryByPlanAsync)}" +
                    $"<{_resultType.FullNameInCode()}>({_spec.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
            }
        }

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _spec;

        if (_batchQuery != null)
        {
            yield return _batchQuery;
        }
        else
        {
            _session = chain.FindVariable(typeof(IQuerySession));
            yield return _session;

            _cancellation = chain.FindVariable(typeof(CancellationToken));
            yield return _cancellation;
        }
    }

    private enum SpecKind
    {
        Compiled,
        Plan
    }
}
