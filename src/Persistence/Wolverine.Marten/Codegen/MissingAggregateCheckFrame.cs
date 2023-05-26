using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten.Events;

namespace Wolverine.Marten.Codegen;

internal class MissingAggregateCheckFrame : SyncFrame
{
    private readonly MemberInfo _aggregateIdMember;
    private readonly Type _aggregateType;
    private readonly Type _commandType;
    private readonly Variable _eventStream;
    private Variable? _command;

    public MissingAggregateCheckFrame(Type aggregateType, Type commandType, MemberInfo aggregateIdMember,
        Variable eventStream)
    {
        _aggregateType = aggregateType;
        _commandType = commandType;
        _aggregateIdMember = aggregateIdMember;
        _eventStream = eventStream;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _command = chain.FindVariable(_commandType);
        yield return _command;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(
            $"if ({_eventStream.Usage}.{nameof(IEventStream<string>.Aggregate)} == null) throw new {typeof(UnknownAggregateException).FullNameInCode()}(typeof({_aggregateType.FullNameInCode()}), {_command!.Usage}.{_aggregateIdMember.Name});");

        Next?.GenerateCode(method, writer);
    }
}