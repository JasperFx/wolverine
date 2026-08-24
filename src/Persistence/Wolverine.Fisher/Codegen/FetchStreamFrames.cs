using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Fisher;

namespace Wolverine.Fisher.Codegen;

// GH-3627. The Fisher spellings of [StreamState] and [StreamEvents], reached through
// IEventSourcingFrameProvider so core never writes "session.Events.FetchStreamStateAsync(...)" itself.
//
// Standalone rather than batched, deliberately: Fisher's own FetchLatestAggregateFrame is a plain
// AsyncFrame with no IBatchableFrame, so batching these would make the raw stream reads behave
// differently from the aggregate read right next to them. Marten batches because Marten's fetch-latest
// already does.
internal abstract class FetchStreamFrameBase : AsyncFrame
{
    protected readonly Variable _identity;
    protected Variable _session = null!;
    protected Variable _token = null!;

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

    protected abstract string Call { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;

        yield return _identity;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Read.Usage} = await {_session.Usage}.Events.{Call}({_identity.Usage}, {_token.Usage});");
        Next?.GenerateCode(method, writer);
    }
}

internal class FetchStreamStateFrame : FetchStreamFrameBase
{
    public FetchStreamStateFrame(Variable identity) : base(identity, typeof(StreamState))
    {
    }

    protected override string Call => nameof(IQueryEventStore.FetchStreamStateAsync);
}

internal class FetchStreamFrame : FetchStreamFrameBase
{
    public FetchStreamFrame(Variable identity) : base(identity, typeof(IReadOnlyList<IEvent>))
    {
    }

    protected override string Call => nameof(IQueryEventStore.FetchStreamAsync);
}
