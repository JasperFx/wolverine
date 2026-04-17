using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Weasel.EntityFrameworkCore.Batching;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
/// Codegen frame that executes a Wolverine.EntityFrameworkCore
/// <see cref="IQueryPlan{TDbContext,TResult}"/> (and optionally
/// <see cref="IBatchQueryPlan{TDbContext,TResult}"/>) and produces the
/// materialized result as a new variable for downstream frames to consume.
///
/// <para>
/// When the spec type implements <see cref="IBatchQueryPlan{TDbContext,TResult}"/>
/// (including all types derived from <see cref="QueryPlan{TDbContext,TEntity}"/>
/// and <see cref="QueryListPlan{TDbContext,TEntity}"/>), this frame routes
/// through <see cref="EFCoreBatchingPolicy"/> to share a single
/// <see cref="BatchedQuery"/> with other batchable loads on the same handler.
/// </para>
/// </summary>
internal class FetchSpecificationFrame : AsyncFrame, IEFCoreBatchableFrame
{
    private readonly Variable _spec;
    private readonly Type _dbContextType;
    private readonly Type _resultType;

    private Variable? _dbContext;
    private Variable? _cancellation;
    private Variable? _batchQuery;
    private Variable? _batchItem;

    public FetchSpecificationFrame(Variable specVar)
    {
        _spec = specVar ?? throw new ArgumentNullException(nameof(specVar));

        var specType = specVar.VariableType;

        // Look for IBatchQueryPlan<TDbContext, TResult> FIRST — if the spec implements
        // it, the batching path is always available and preferred by the batching policy.
        var batchPlan = specType.FindInterfaceThatCloses(typeof(IBatchQueryPlan<,>));
        var queryPlan = specType.FindInterfaceThatCloses(typeof(IQueryPlan<,>));

        var chosenInterface = batchPlan ?? queryPlan
            ?? throw new ArgumentException(
                $"Type {specType.FullName} does not implement Wolverine.EntityFrameworkCore.IQueryPlan<,> or IBatchQueryPlan<,>.",
                nameof(specVar));

        var args = chosenInterface.GetGenericArguments();
        _dbContextType = args[0];
        _resultType = args[1];
        CanBatch = batchPlan is not null;

        var resultName = $"{specVar.Usage}_result";
        Result = new Variable(_resultType, resultName, this);
    }

    /// <summary>
    /// True if the spec implements <see cref="IBatchQueryPlan{TDbContext,TResult}"/>
    /// and can participate in a shared <see cref="BatchedQuery"/>.
    /// </summary>
    public bool CanBatch { get; }

    /// <summary>
    /// The DbContext type from the spec's generic closure (e.g. <c>OrderDbContext</c>).
    /// Used by <see cref="EFCoreBatchingPolicy"/> to group specs by context — only
    /// specs against the same DbContext can share a batch.
    /// </summary>
    public Type DbContextType => _dbContextType;

    /// <summary>
    /// Materialized result variable downstream frames bind to by type.
    /// </summary>
    public Variable Result { get; }

    // ── IEFCoreBatchableFrame ──────────────────────────────────────────

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
        if (_batchItem == null || _batchQuery == null || _dbContext == null) return;

        // spec.FetchAsync(batch, dbContext)  — delegates to plan's IBatchQueryPlan impl,
        // which in turn calls batch.QuerySingle / batch.Query on the plan's IQueryable.
        writer.WriteLine(
            $"var {_batchItem.Usage} = " +
            $"(({typeof(IBatchQueryPlan<,>).MakeGenericType(_dbContextType, _resultType).FullNameInCode()}){_spec.Usage})" +
            $".{nameof(IBatchQueryPlan<DbContext, object>.FetchAsync)}({_batchQuery.Usage}, {_dbContext.Usage});");
    }

    // ── Frame ─────────────────────────────────────────────────────────

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchItem != null)
        {
            // Batched path — batch.ExecuteAsync has already run by the time we reach here.
            writer.WriteLine($"var {Result.Usage} = await {_batchItem.Usage}.ConfigureAwait(false);");
        }
        else
        {
            // Standalone — direct IQueryPlan.FetchAsync call
            writer.WriteLine(
                $"var {Result.Usage} = await {_spec.Usage}" +
                $".{nameof(IQueryPlan<DbContext, object>.FetchAsync)}({_dbContext!.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _spec;

        _dbContext = chain.FindVariable(_dbContextType);
        yield return _dbContext;

        if (_batchQuery != null)
        {
            yield return _batchQuery;
        }
        else
        {
            _cancellation = chain.FindVariable(typeof(CancellationToken));
            yield return _cancellation;
        }
    }
}
