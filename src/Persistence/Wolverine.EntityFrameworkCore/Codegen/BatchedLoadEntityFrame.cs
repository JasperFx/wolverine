using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Weasel.EntityFrameworkCore.Batching;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
/// Code generation frame that loads an entity using Weasel's <see cref="BatchedQuery"/>
/// API instead of <c>DbContext.FindAsync</c>. This enables multiple entity loads within
/// the same handler to be combined into a single database round trip by sharing a
/// <see cref="BatchedQuery"/> instance.
///
/// Usage: replace <see cref="LoadEntityFrame"/> with this frame when you want batch
/// query participation. A <see cref="CreateBatchQueryFrame"/> must appear earlier in
/// the middleware chain to supply the <c>BatchedQuery</c> variable, and an
/// <see cref="ExecuteBatchQueryFrame"/> must appear after all batch loads.
/// </summary>
// AOT note (#2746): Task<>.MakeGenericType(sagaType) at codegen time. Same
// chunk M (LoggerVariableSource) / chunk P (saga frame providers) pattern.
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = "Task<>.MakeGenericType over runtime saga type at codegen; AOT consumers run pre-generated frames in TypeLoadMode.Static. See AOT guide.")]
internal class BatchedLoadEntityFrame : SyncFrame
{
    private readonly Type _dbContextType;
    private readonly Variable _sagaId;
    private readonly string _pkPropertyName;
    private Variable? _context;
    private Variable? _batchQuery;

    public BatchedLoadEntityFrame(Type dbContextType, Type sagaType, Variable sagaId, string pkPropertyName)
    {
        _dbContextType = dbContextType;
        _sagaId = sagaId;
        _pkPropertyName = pkPropertyName;

        Saga = new Variable(sagaType, this);
        // The task that will be awaited by ExecuteBatchQueryFrame
        SagaTask = new Variable(typeof(Task<>).MakeGenericType(sagaType), sagaType.Name.ToLowerInvariant() + "Task", this);
    }

    public Variable Saga { get; }

    /// <summary>
    /// The pending <see cref="Task{T}"/> returned by <c>batch.QuerySingle()</c>.
    /// Must be awaited by the consumer (e.g. <see cref="ExecuteBatchQueryFrame"/>)
    /// after <c>batch.ExecuteAsync()</c>.
    /// </summary>
    public Variable SagaTask { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(_dbContextType);
        yield return _context;

        var batch = chain.TryFindVariable(typeof(BatchedQuery), VariableSource.All);
        if (batch != null)
        {
            _batchQuery = batch;
            yield return _batchQuery;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Queue entity load into a BatchedQuery for a single-round-trip multi-entity fetch");

        if (_batchQuery != null)
        {
            writer.WriteLine(
                $"var {SagaTask.Usage} = {_batchQuery.Usage}.{nameof(BatchedQuery.QuerySingle)}(" +
                $"{_context!.Usage}.{nameof(DbContext.Set)}<{Saga.VariableType.FullNameInCode()}>().Where(x => x.{_pkPropertyName} == {_sagaId.Usage}));");
        }
        else
        {
            // Fallback: create a local single-use batch (no sharing benefit, but still correct)
            writer.WriteLine(
                $"var {Saga.VariableType.Name.ToLowerInvariant()}Batch = {_context!.Usage}.{nameof(BatchQueryExtensions.CreateBatchQuery)}();");
            writer.WriteLine(
                $"var {SagaTask.Usage} = {Saga.VariableType.Name.ToLowerInvariant()}Batch.{nameof(BatchedQuery.QuerySingle)}(" +
                $"{_context!.Usage}.{nameof(DbContext.Set)}<{Saga.VariableType.FullNameInCode()}>().Where(x => x.{_pkPropertyName} == {_sagaId.Usage}));");
        }

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Frame that creates a shared <see cref="BatchedQuery"/> instance to be shared
/// by multiple <see cref="BatchedLoadEntityFrame"/> instances in the same handler chain.
/// Insert this frame before any <see cref="BatchedLoadEntityFrame"/> frames.
/// </summary>
internal class CreateBatchQueryFrame : SyncFrame
{
    private Variable? _context;
    private readonly Type _dbContextType;

    public CreateBatchQueryFrame(Type dbContextType)
    {
        _dbContextType = dbContextType;
        BatchQuery = new Variable(typeof(BatchedQuery), "batchQuery", this);
    }

    public Variable BatchQuery { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(_dbContextType);
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Create a shared BatchedQuery so multiple entity loads share one round trip");
        writer.WriteLine(
            $"var {BatchQuery.Usage} = {_context!.Usage}.{nameof(BatchQueryExtensions.CreateBatchQuery)}();");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Frame that executes a shared <see cref="BatchedQuery"/> and then awaits all
/// pending entity-load tasks queued by <see cref="BatchedLoadEntityFrame"/> instances.
/// Insert this frame after all <see cref="BatchedLoadEntityFrame"/> frames.
/// </summary>
internal class ExecuteBatchQueryFrame : AsyncFrame
{
    private readonly Variable _batchQuery;
    private readonly IReadOnlyList<BatchedLoadEntityFrame> _loadFrames;
    private Variable? _cancellation;

    public ExecuteBatchQueryFrame(Variable batchQuery, IReadOnlyList<BatchedLoadEntityFrame> loadFrames)
    {
        _batchQuery = batchQuery;
        _loadFrames = loadFrames;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _batchQuery;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        foreach (var frame in _loadFrames)
        {
            yield return frame.SagaTask;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Execute all queued batch queries in a single database round trip");
        writer.WriteLine(
            $"await {_batchQuery.Usage}.{nameof(BatchedQuery.ExecuteAsync)}({_cancellation!.Usage}).ConfigureAwait(false);");

        foreach (var frame in _loadFrames)
        {
            writer.WriteLine(
                $"var {frame.Saga.Usage} = await {frame.SagaTask.Usage}.ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
    }
}
