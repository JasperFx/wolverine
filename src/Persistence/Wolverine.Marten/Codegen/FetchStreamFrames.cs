using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Marten;
using Marten.Services.BatchQuerying;

namespace Wolverine.Marten.Codegen;

// GH-3627. The Marten spellings of [StreamState] and [StreamEvents], reached through
// IEventSourcingFrameProvider so that core never writes "session.Events.FetchStreamStateAsync(...)"
// itself -- which is what keeps the batch-query enlistment below on Marten's side of the seam, exactly
// as FetchLatestAggregateFrame does for [ReadModel].
internal abstract class FetchStreamFrameBase : AsyncFrame, IBatchableFrame
{
    protected readonly Variable _identity;
    protected Variable _session = null!;
    protected Variable _token = null!;
    protected Variable _batchQuery = null!;
    protected Variable _batchQueryItem = null!;

    protected FetchStreamFrameBase(Variable identity, Type readType)
    {
        if (identity.VariableType == typeof(Guid) || identity.VariableType == typeof(string))
        {
            _identity = identity;
        }
        else
        {
            var valueType = ValueTypeInfo.ForType(identity.VariableType);
            _identity = new MemberAccessVariable(identity, valueType.ValueProperty);
        }

        Read = new Variable(readType, this);
    }

    public Variable Read { get; }

    protected abstract string BatchCall { get; }

    protected abstract string StandaloneCall { get; }

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
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;

        if (_batchQuery != null)
        {
            yield return _batchQuery;
        }

        yield return _identity;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchQueryItem == null)
        {
            writer.Write($"var {Read.Usage} = await {_session.Usage}.Events.{StandaloneCall}({_identity.Usage}, {_token.Usage});");
        }
        else
        {
            writer.Write($"var {Read.Usage} = await {_batchQueryItem.Usage}.ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
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
