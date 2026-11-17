using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;
using Marten;
using Marten.Services.BatchQuerying;

namespace Wolverine.Marten.Codegen;

// GH-3627. The Marten spellings of [StreamState] and [StreamEvents], reached through
// IEventSourcingFrameProvider so that core never writes "session.Events.FetchStreamStateAsync(...)"
// itself -- which is what keeps the batch-query enlistment below on Marten's side of the seam, exactly
// as FetchLatestAggregateFrame does for [ReadModel]. GH-5141: the standalone read, in C# and F#, is
// Wolverine core's; Polecat and Fisher use core's frames directly.
internal abstract class FetchStreamFrameBase : Wolverine.Persistence.EventSourcing.Codegen.FetchStreamFrameBase,
    IBatchableFrame
{
    protected Variable? _batchQuery;
    protected Variable? _batchQueryItem;

    protected FetchStreamFrameBase(Variable identity, Type readType)
        : base(identity, readType, typeof(IDocumentSession))
    {
    }

    protected abstract string BatchCall { get; }

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchQueryItem == null)
            throw new InvalidOperationException("This frame has not been enlisted in a MartenBatchFrame");

        writer.Write($"var {_batchQueryItem.Usage} = {_batchQuery!.Usage}.Events.{BatchCall}({_identity.Usage});");
    }

    public void EnlistInBatchQuery(Variable batchQuery)
    {
        _batchQueryItem = new Variable(typeof(Task<>).MakeGenericType(Read.VariableType),
            Read.Usage + "_BatchItem", this);
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

        writer.Write($"var {Read.Usage} = await {_batchQueryItem.Usage}.ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchQueryItem == null)
        {
            base.GenerateFSharpCode(method, writer);
            return;
        }

        writer.WriteLine($"let! {Read.Usage} = {_batchQueryItem.FSharpUsage}");
        Next?.GenerateFSharpCode(method, writer);
    }
}

internal class FetchStreamStateFrame : FetchStreamFrameBase
{
    public FetchStreamStateFrame(Variable identity) : base(identity, typeof(StreamState))
    {
    }

    protected override string BatchCall => nameof(IBatchEvents.FetchStreamState);

    protected override string StandaloneCall => nameof(IQueryEventStore.FetchStreamStateAsync);
}

internal class FetchStreamFrame : FetchStreamFrameBase
{
    public FetchStreamFrame(Variable identity) : base(identity, typeof(IReadOnlyList<IEvent>))
    {
    }

    protected override string BatchCall => nameof(IBatchEvents.FetchStream);

    protected override string StandaloneCall => nameof(IQueryEventStore.FetchStreamAsync);
}
