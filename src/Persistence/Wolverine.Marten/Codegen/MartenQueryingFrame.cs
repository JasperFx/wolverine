using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;
using Marten.Services.BatchQuerying;
using Wolverine.Persistence;

namespace Wolverine.Marten.Codegen;

internal interface IBatchableFrame
{
    void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer);
    void EnlistInBatchQuery(Variable batchQuery);
}

internal class MartenBatchingPolicy : IMethodPreCompilationPolicy
{
    public void Apply(IGeneratedMethod method)
    {
        var (i, frames) = sortThroughFrames(method);
        if (frames.Count <= 1) return;
        
        var batchFrame = new MartenBatchFrame();
        method.Frames.Insert(i, batchFrame);

        foreach (var frame in frames)
        {
            batchFrame.Enlist(frame);
        }
    }

    private static bool IsBatchable(IBatchableFrame frame)
    {
        // Natural key aggregate loads cannot be batched because IBatchedQuery
        // does not have a FetchForWriting<T, TNaturalKey> overload
        if (frame is LoadAggregateFrame laf && laf.IsNaturalKey) return false;
        return true;
    }

    private static (int, IReadOnlyList<IBatchableFrame> frames) sortThroughFrames(IGeneratedMethod method)
    {
        var list = new List<IBatchableFrame>();
        
        var index = -1;
        for (int i = 0; i < method.Frames.Count; i++)
        {
            var frame = method.Frames[i];
            if (frame is LoadEntityFrameBlock block && block.Creator is IBatchableFrame b && IsBatchable(b))
            {
                list.Add(b);
                if (index == -1)
                {
                    index = i;
                }
            }
            else if (frame is IBatchableFrame batchable && IsBatchable(batchable))
            {
                list.Add(batchable);
                if (index == -1)
                {
                    index = i;
                }
            }
        }

        return (index, list);
    }
}

internal class MartenBatchFrame : AsyncFrame
{
    private Variable _session = null!;
    private Variable _cancellation = null!;

    private List<IBatchableFrame> _operations = new();

    public MartenBatchFrame()
    {
        BatchQuery = new Variable(typeof(IBatchedQuery), this);
    }   

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"var {BatchQuery.Usage} = {_session.Usage}.{nameof(IQuerySession.CreateBatchQuery)}();");
        foreach (var op in _operations)
        {
            writer.BlankLine();
            op.WriteCodeToEnlistInBatchQuery(method, writer);
        }

        writer.BlankLine();

        writer.WriteLine($"await {BatchQuery.Usage}.{nameof(IBatchedQuery.Execute)}({_cancellation.Usage});");

        writer.BlankLine();

        // After the batch query executes, generate the code to resolve each
        // batched frame's result variable (e.g. var stream_entity = await stream_entity_BatchItem).
        // This must happen BEFORE any guard frames that reference these variables.
        foreach (var op in _operations)
        {
            if (op is LoadAggregateFrame loadFrame)
            {
                loadFrame.GenerateCodeForBatchResolution(method, writer);
            }
        }

        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
    
    public Variable BatchQuery { get; }

    public void Enlist(IBatchableFrame frame)
    {
        if (_operations.Contains(frame)) return;
        
        frame.EnlistInBatchQuery(BatchQuery);
        _operations.Add(frame);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
        
        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        // This ensures those variables are declared before we try to use them in the batch enlistment code
        foreach (var op in _operations)
        {
            if (op is Frame frame)
            {
                foreach (var variable in frame.FindVariables(chain))
                {
                    yield return variable;
                }
            }
        }
    }
}