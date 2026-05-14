using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Polecat;
using Polecat.Batching;
using Wolverine.Persistence;

namespace Wolverine.Polecat.Codegen;

internal interface IBatchableFrame
{
    void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer);
    void EnlistInBatchQuery(Variable batchQuery);
}

internal class PolecatBatchingPolicy : IMethodPreCompilationPolicy
{
    public void Apply(IGeneratedMethod method)
    {
        var (i, frames) = SortThroughFrames(method);
        if (frames.Count <= 1) return;

        var batchFrame = new PolecatBatchFrame();
        method.Frames.Insert(i, batchFrame);

        foreach (var frame in frames)
        {
            batchFrame.Enlist(frame);
        }
    }

    internal static (int, IReadOnlyList<IBatchableFrame> frames) SortThroughFrames(IGeneratedMethod method)
    {
        var list = new List<IBatchableFrame>();

        var index = -1;
        for (int i = 0; i < method.Frames.Count; i++)
        {
            var frame = method.Frames[i];
            if (frame is LoadEntityFrameBlock block && block.Creator is IBatchableFrame b)
            {
                list.Add(b);
                if (index == -1)
                {
                    index = i;
                }
            }
            else if (frame is IBatchableFrame batchable)
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

internal class PolecatBatchFrame : AsyncFrame
{
    private Variable _session = null!;
    private Variable _cancellation = null!;

    private List<IBatchableFrame> _operations = new();

    public PolecatBatchFrame()
    {
        BatchQuery = new Variable(typeof(IBatchedQuery), this);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(
            $"var {BatchQuery.Usage} = {_session.Usage}.{nameof(IQuerySession.CreateBatchQuery)}();");
        foreach (var op in _operations)
        {
            writer.BlankLine();
            op.WriteCodeToEnlistInBatchQuery(method, writer);
        }

        writer.BlankLine();

        writer.WriteLine(
            $"await {BatchQuery.Usage}.{nameof(IBatchedQuery.Execute)}({_cancellation.Usage});");

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
