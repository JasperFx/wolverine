using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Services.BatchQuerying;

namespace Wolverine.Marten.Codegen;

// GH-3907: reached through IEventSourcingFrameProvider.BuildFetchLatestFrame, which is what keeps the batch-query
// enlistment below on Marten's side of the seam. GH-5141: the standalone read, in C# and F#, is Wolverine core's.
internal class FetchLatestAggregateFrame : Wolverine.Persistence.EventSourcing.Codegen.FetchLatestAggregateFrame,
    IBatchableFrame
{
    private Variable? _batchQuery;
    private Variable? _batchQueryItem;

    public FetchLatestAggregateFrame(Type aggregateType, Variable identity)
        : base(aggregateType, identity, typeof(IDocumentSession))
    {
    }

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchQueryItem == null)
            throw new InvalidOperationException("This frame has not been enlisted in a MartenBatchFrame");

        writer.Write(
            $"var {_batchQueryItem.Usage} = {_batchQuery!.Usage}.Events.{nameof(IBatchEvents.FetchLatest)}<{Aggregate.VariableType.FullNameInCode()}>({_identity.Usage});");
    }

    public void EnlistInBatchQuery(Variable batchQuery)
    {
        _batchQueryItem = new Variable(typeof(Task<>).MakeGenericType(Aggregate.VariableType), Aggregate.Usage + "_BatchItem",
            this);
        _batchQuery = batchQuery;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var variable in base.FindVariables(chain))
        {
            yield return variable;
        }

        if (_batchQuery != null)
        {
            yield return _batchQuery;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchQueryItem == null)
        {
            base.GenerateCode(method, writer);
            return;
        }

        writer.Write(
            $"var {Aggregate.Usage} = await {_batchQueryItem.Usage}.ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchQueryItem == null)
        {
            base.GenerateFSharpCode(method, writer);
            return;
        }

        writer.WriteLine($"let! {Aggregate.Usage} = {_batchQueryItem.FSharpUsage}");
        Next?.GenerateFSharpCode(method, writer);
    }
}
