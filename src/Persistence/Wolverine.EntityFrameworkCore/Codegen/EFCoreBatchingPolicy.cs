using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Weasel.EntityFrameworkCore.Batching;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
/// Marker interface for frames that can enlist their work into a Weasel
/// <see cref="BatchedQuery"/> — the EF Core counterpart to the Marten
/// <c>IBatchableFrame</c> in Wolverine.Marten.Codegen. Implemented by
/// <see cref="FetchSpecificationFrame"/> (batch-capable plans) so
/// <see cref="EFCoreBatchingPolicy"/> can group multiple specs into one
/// database round-trip.
/// </summary>
internal interface IEFCoreBatchableFrame
{
    /// <summary>True if this frame is eligible for batching.</summary>
    bool CanBatch { get; }

    /// <summary>DbContext type the frame targets — only frames against the same
    /// context are batched together.</summary>
    Type DbContextType { get; }

    /// <summary>Called by <see cref="EFCoreBatchFrame"/> to bind a shared
    /// <see cref="BatchedQuery"/> variable; the frame creates its pending
    /// <see cref="Task{T}"/> variable here.</summary>
    void EnlistInBatchQuery(Variable batchQuery);

    /// <summary>Emits the code that queues this frame's work into the shared
    /// batch (e.g. <c>var foo_BatchItem = spec.FetchAsync(batch, db);</c>).</summary>
    void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer);
}

/// <summary>
/// Method pre-compilation policy that groups multiple batch-capable EF Core
/// frames on the same handler and the same DbContext into a single shared
/// <see cref="BatchedQuery"/> round-trip. Mirrors the design of
/// <c>MartenBatchingPolicy</c>.
/// </summary>
internal class EFCoreBatchingPolicy : IMethodPreCompilationPolicy
{
    public void Apply(IGeneratedMethod method)
    {
        // Group batchable frames by DbContext type — a single handler may touch
        // multiple DbContexts and each needs its own batch.
        var batchable = new List<(int Index, IEFCoreBatchableFrame Frame)>();
        for (var i = 0; i < method.Frames.Count; i++)
        {
            if (method.Frames[i] is IEFCoreBatchableFrame bf && bf.CanBatch)
            {
                batchable.Add((i, bf));
            }
        }

        if (batchable.Count < 2) return;

        var groups = batchable
            .GroupBy(x => x.Frame.DbContextType)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var ops = group.OrderBy(x => x.Index).ToList();
            var insertionIndex = method.Frames.IndexOf((Frame)ops[0].Frame);

            var batchFrame = new EFCoreBatchFrame(group.Key);
            method.Frames.Insert(insertionIndex, batchFrame);

            foreach (var (_, frame) in ops)
            {
                batchFrame.Enlist(frame);
            }
        }
    }
}

/// <summary>
/// Generated frame that creates a shared <see cref="BatchedQuery"/>, emits each
/// enlisted frame's enlistment code, and executes the batch with one
/// round-trip. Subsequent <see cref="FetchSpecificationFrame"/>s in the chain
/// resolve their pending <c>Task&lt;T&gt;</c>s into result variables.
/// </summary>
internal class EFCoreBatchFrame : AsyncFrame
{
    private readonly Type _dbContextType;
    private Variable? _dbContext;
    private Variable? _cancellation;
    private readonly List<IEFCoreBatchableFrame> _operations = new();

    public EFCoreBatchFrame(Type dbContextType)
    {
        _dbContextType = dbContextType;
        BatchQuery = new Variable(typeof(BatchedQuery), this);
    }

    /// <summary>Shared <see cref="BatchedQuery"/> variable.</summary>
    public Variable BatchQuery { get; }

    public void Enlist(IEFCoreBatchableFrame frame)
    {
        if (_operations.Contains(frame)) return;
        frame.EnlistInBatchQuery(BatchQuery);
        _operations.Add(frame);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(
            $"var {BatchQuery.Usage} = {typeof(BatchQueryExtensions).FullNameInCode()}" +
            $".{nameof(BatchQueryExtensions.CreateBatchQuery)}({_dbContext!.Usage});");

        foreach (var op in _operations)
        {
            writer.BlankLine();
            op.WriteCodeToEnlistInBatchQuery(method, writer);
        }

        writer.BlankLine();
        writer.WriteLine(
            $"await {BatchQuery.Usage}.{nameof(BatchedQuery.ExecuteAsync)}({_cancellation!.Usage}).ConfigureAwait(false);");
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _dbContext = chain.FindVariable(_dbContextType);
        yield return _dbContext;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        // Make sure inputs to enlisted frames are declared before the batch emission
        foreach (var op in _operations)
        {
            if (op is Frame frame)
            {
                foreach (var v in frame.FindVariables(chain))
                {
                    yield return v;
                }
            }
        }
    }
}
