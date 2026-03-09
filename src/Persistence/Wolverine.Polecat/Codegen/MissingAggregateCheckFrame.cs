using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Polecat.Events;

namespace Wolverine.Polecat.Codegen;

internal class MissingAggregateCheckFrame : SyncFrame
{
    private readonly Type _aggregateType;
    private readonly Variable _identity;
    private readonly Variable _eventStream;

    public MissingAggregateCheckFrame(Type aggregateType, Variable identity,
        Variable eventStream)
    {
        _aggregateType = aggregateType;
        _identity = identity;
        _eventStream = eventStream;

        uses.Add(identity);
        uses.Add(eventStream);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield break;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(
            $"if ({_eventStream.Usage}.{nameof(IEventStream<string>.Aggregate)} == null) throw new {typeof(UnknownAggregateException).FullNameInCode()}(typeof({_aggregateType.FullNameInCode()}), {_identity.Usage});");

        Next?.GenerateCode(method, writer);
    }
}
