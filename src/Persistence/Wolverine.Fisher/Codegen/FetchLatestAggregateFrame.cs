using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Fisher;

namespace Wolverine.Fisher.Codegen;

// GH-3907: unchanged, but it moved out of ReadAggregateAttribute.cs when that attribute became a shell
// over Wolverine core's [ReadModel]. The attribute no longer names this type - it reaches it through
// IEventSourcingFrameProvider.BuildFetchLatestFrame, which is what keeps "session.Events.FetchLatest"
// on Fisher's side of the seam.
internal class FetchLatestAggregateFrame : AsyncFrame
{
    private readonly Variable _identity;
    private Variable _session = null!;
    private Variable _token = null!;

    public FetchLatestAggregateFrame(Type aggregateType, Variable identity)
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

        Aggregate = new Variable(aggregateType, this);
    }

    public Variable Aggregate { get; }

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
        writer.Write($"var {Aggregate.Usage} = await {_session.Usage}.Events.FetchLatest<{Aggregate.VariableType.FullNameInCode()}>({_identity.Usage}, {_token.Usage});");
        Next?.GenerateCode(method, writer);
    }
}
