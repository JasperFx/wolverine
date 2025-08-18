using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Marten;

/// <summary>
///     Applies middleware to Wolverine message actions to apply a workflow with concurrency protections for
///     "command" messages that use a Marten projected aggregate to "decide" what
///     on new events to persist to the aggregate stream.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AggregateHandlerAttribute : ModifyChainAttribute, IDataRequirement
{
    public AggregateHandlerAttribute(ConcurrencyStyle loadStyle)
    {
        LoadStyle = loadStyle;
    }

    public AggregateHandlerAttribute() : this(ConcurrencyStyle.Optimistic)
    {
    }

    internal ConcurrencyStyle LoadStyle { get; }

    /// <summary>
    ///     Override or "help" Wolverine to understand which type is the aggregate type
    /// </summary>
    public Type? AggregateType { get; set; }

    internal MemberInfo? AggregateIdMember { get; set; }
    internal Type? CommandType { get; private set; }

    public MemberInfo? VersionMember { get; private set; }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        // ReSharper disable once CanSimplifyDictionaryLookupWithTryAdd
        if (chain.Tags.ContainsKey(nameof(AggregateHandlerAttribute)))
        {
            return;
        }

        chain.Tags.Add(nameof(AggregateHandlerAttribute), "true");

        CommandType = chain.InputType();
        if (CommandType == null)
        {
            throw new InvalidOperationException(
                $"Cannot apply Marten aggregate handler workflow to chain {chain} because it has no input type");
        }

        AggregateType ??= AggregateHandling.DetermineAggregateType(chain);

        (AggregateIdMember, VersionMember) =
            AggregateHandling.DetermineAggregateIdAndVersion(AggregateType, CommandType, container);
        
        

        var aggregateFrame = new MemberAccessFrame(CommandType, AggregateIdMember,
            $"{Variable.DefaultArgName(AggregateType)}_Id");
        
        var versionFrame = VersionMember == null ? null : new MemberAccessFrame(CommandType,VersionMember, $"{Variable.DefaultArgName(CommandType)}_Version");

        var handling = new AggregateHandling(this)
        {
            AggregateType = AggregateType,
            AggregateId = aggregateFrame.Variable,
            LoadStyle = LoadStyle,
            Version = versionFrame?.Variable
        };
        
        handling.Apply(chain, container);
    }

    public bool Required { get; set; }
    public string MissingMessage { get; set; }
    public OnMissing OnMissing { get; set; }
}

internal class ApplyEventsFromAsyncEnumerableFrame<T> : AsyncFrame, IReturnVariableAction
{
    private readonly Variable _returnValue;
    private Variable? _stream;

    public ApplyEventsFromAsyncEnumerableFrame(Variable returnValue)
    {
        _returnValue = returnValue;
        uses.Add(_returnValue);
    }

    public string Description => "Apply events to Marten event stream";

    public new IEnumerable<Type> Dependencies()
    {
        yield break;
    }

    public IEnumerable<Frame> Frames()
    {
        yield return this;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _stream = chain.FindVariable(typeof(IEventStream<T>));
        yield return _stream;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var variableName = (typeof(T).Name + "Event").ToCamelCase();

        writer.WriteComment(Description);
        writer.Write(
            $"await foreach (var {variableName} in {_returnValue.Usage}) {_stream!.Usage}.{nameof(IEventStream<string>.AppendOne)}({variableName});");
        Next?.GenerateCode(method, writer);
    }
}

internal class EventCaptureActionSource : IReturnVariableActionSource
{
    private readonly Type _aggregateType;

    public EventCaptureActionSource(Type aggregateType)
    {
        _aggregateType = aggregateType;
    }

    public IReturnVariableAction Build(IChain chain, Variable variable)
    {
        return new ActionSource(_aggregateType, variable);
    }

    internal class ActionSource : IReturnVariableAction
    {
        private readonly Type _aggregateType;
        private readonly Variable _variable;

        public ActionSource(Type aggregateType, Variable variable)
        {
            _aggregateType = aggregateType;
            _variable = variable;
        }

        public string Description => "Append event to event stream for aggregate " + _aggregateType.FullNameInCode();

        public IEnumerable<Type> Dependencies()
        {
            yield break;
        }

        public IEnumerable<Frame> Frames()
        {
            var streamType = typeof(IEventStream<>).MakeGenericType(_aggregateType);

            yield return new MethodCall(streamType, nameof(IEventStream<string>.AppendOne))
            {
                Arguments =
                {
                    [0] = _variable
                }
            };
        }
    }
}