using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
/// Guards the case where the handler method lives on the aggregate itself: if the stream has no
/// aggregate, there is no target to invoke the handler on, so fail with the store's own
/// "unknown aggregate" exception rather than an NRE inside generated code.
/// </summary>
/// <remarks>
/// GH-3907: was duplicated per store. The only store-specific thing about it was which exception type
/// the generated code throws, so that comes over the seam as
/// <see cref="IEventSourcingFrameProvider.UnknownAggregateExceptionType"/> — which keeps the generated
/// source byte-identical, and keeps user code that catches <c>Wolverine.Marten.UnknownAggregateException</c>
/// (or the Polecat one) working exactly as before.
/// </remarks>
internal class MissingAggregateCheckFrame : SyncFrame
{
    private readonly Type _aggregateType;
    private readonly Variable _identity;
    private readonly Variable _eventStream;
    private readonly Type _exceptionType;

    public MissingAggregateCheckFrame(Type aggregateType, Variable identity, Variable eventStream, Type exceptionType)
    {
        _aggregateType = aggregateType;
        _identity = identity;
        _eventStream = eventStream;
        _exceptionType = exceptionType;

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
            $"if ({_eventStream.Usage}.{nameof(IEventStream<string>.Aggregate)} == null) throw new {_exceptionType.FullNameInCode()}(typeof({_aggregateType.FullNameInCode()}), {_identity.Usage});");

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(
            $"if isNull {_eventStream.FSharpUsage}.{nameof(IEventStream<string>.Aggregate)} then raise({_exceptionType.FSharpName()}(typeof<{_aggregateType.FSharpName()}>, {_identity.FSharpUsage}))");

        Next?.GenerateFSharpCode(method, writer);
    }
}
