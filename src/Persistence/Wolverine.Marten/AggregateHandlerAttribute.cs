using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using Marten.Linq.Members;
using Marten.Schema;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;
using Wolverine.Codegen;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Publishing;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Marten;

/// <summary>
/// Tells Wolverine handlers that this value contains a
/// list of events to be appended to the current stream
/// </summary>
public class Events : List<object>, IWolverineReturnType
{
    public static Events operator +(Events events, object @event)
    {
        events.Add(@event);
        return events;
    }
}

/// <summary>
///     Applies middleware to Wolverine message actions to apply a workflow with concurrency protections for
///     "command" messages that use a Marten projected aggregate to "decide" what
///     on new events to persist to the aggregate stream.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AggregateHandlerAttribute : ModifyChainAttribute
{
    private static readonly Type _versioningBaseType = typeof(AggregateVersioning<>);

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
        if (chain.Tags.ContainsKey(nameof(AggregateHandlerAttribute))) return;
        
        chain.Tags.Add(nameof(AggregateHandlerAttribute),"true");

        CommandType = chain.InputType();
        if (CommandType == null)
        {
            throw new InvalidOperationException(
                $"Cannot apply Marten aggregate handler workflow to chain {chain} because it has no input type");
        }
        
        AggregateType ??= DetermineAggregateType(chain);

        if (CommandType.Closes(typeof(IEvent<>)))
        {
            var concreteEventType = typeof(Event<>).MakeGenericType(CommandType.GetGenericArguments()[0]);
            
            // This CANNOT work if you capture the version, because there's no way to know if the aggregate version
            // has advanced
            //VersionMember = concreteEventType.GetProperty(nameof(IEvent.Version));
            
            var options = container.Services.GetRequiredService<StoreOptions>();
            AggregateIdMember = options.Events.StreamIdentity == StreamIdentity.AsGuid
                ? concreteEventType.GetProperty(nameof(IEvent.StreamId), BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                : concreteEventType.GetProperty(nameof(IEvent.StreamKey), BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        }
        else
        {
            AggregateIdMember = DetermineAggregateIdMember(AggregateType, CommandType);
            VersionMember = DetermineVersionMember(CommandType);
        }

        var sessionCreator = MethodCall.For<OutboxedSessionFactory>(x => x.OpenSession(null!));
        chain.Middleware.Add(sessionCreator);

        var firstCall = chain.HandlerCalls().First();

        var loader = generateLoadAggregateCode(chain);
        if (AggregateType == firstCall.HandlerType)
        {
            chain.Middleware.Add(new MissingAggregateCheckFrame(AggregateType, CommandType, AggregateIdMember,
                loader.ReturnVariable!));
        }

        // Use the active document session as an IQuerySession instead of creating a new one
        firstCall.TrySetArgument(new Variable(typeof(IQuerySession), sessionCreator.ReturnVariable!.Usage));

        DetermineEventCaptureHandling(chain, firstCall, AggregateType);

        ValidateMethodSignatureForEmittedEvents(chain, firstCall, chain);
        RelayAggregateToHandlerMethod(loader.ReturnVariable, firstCall, AggregateType);

        chain.Postprocessors.Add(MethodCall.For<IDocumentSession>(x => x.SaveChangesAsync(default)));
        
        new AggregateHandling(AggregateType, new Variable(AggregateIdMember.GetRawMemberType(), "aggregateId")).Store(chain);
    }

    internal static void DetermineEventCaptureHandling(IChain chain, MethodCall firstCall, Type aggregateType)
    {
        var asyncEnumerable = firstCall.Creates.FirstOrDefault(x => x.VariableType == typeof(IAsyncEnumerable<object>));
        if (asyncEnumerable != null)
        {
            asyncEnumerable.UseReturnAction(_ =>
            {
                return typeof(ApplyEventsFromAsyncEnumerableFrame<>).CloseAndBuildAs<Frame>(asyncEnumerable,
                    aggregateType);
            });

            return;
        }

        var eventsVariable = firstCall.Creates.FirstOrDefault(x => x.VariableType == typeof(Events)) ??
                             firstCall.Creates.FirstOrDefault(x =>
                                 x.VariableType.CanBeCastTo<IEnumerable<object>>() &&
                                 !x.VariableType.CanBeCastTo<IWolverineReturnType>());

        if (eventsVariable != null)
        {
            eventsVariable.UseReturnAction(
                v => typeof(RegisterEventsFrame<>).CloseAndBuildAs<MethodCall>(eventsVariable, aggregateType)
                    .WrapIfNotNull(v), "Append events to the Marten event stream");

            return;
        }

        // If there's no return value of Events or IEnumerable<object>, and there's also no parameter of IEventStream<Aggregate>,
        // then assume that the default behavior of each return value is to be an event
        if (!firstCall.Method.GetParameters().Any(x => x.ParameterType.Closes(typeof(IEventStream<>))))
        {
            chain.ReturnVariableActionSource = new EventCaptureActionSource(aggregateType);
        }
    }

    internal static Variable RelayAggregateToHandlerMethod(Variable eventStream, MethodCall firstCall, Type aggregateType)
    {
        var aggregateVariable = new MemberAccessVariable(eventStream,
            typeof(IEventStream<>).MakeGenericType(aggregateType).GetProperty("Aggregate"));

        if (firstCall.HandlerType == aggregateType)
        {
            // If the handle method is on the aggregate itself
            firstCall.Target = aggregateVariable;
        }
        else
        {
            firstCall.TrySetArgument(aggregateVariable);
        }

        return aggregateVariable;
    }

    internal static void ValidateMethodSignatureForEmittedEvents(IChain chain, MethodCall firstCall,
        IChain handlerChain)
    {
        if (firstCall.Method.ReturnType == typeof(Task) || firstCall.Method.ReturnType == typeof(void))
        {
            var parameters = chain.HandlerCalls().First().Method.GetParameters();
            var stream = parameters.FirstOrDefault(x => x.ParameterType.Closes(typeof(IEventStream<>)));
            if (stream == null)
            {
                throw new InvalidOperationException(
                    $"No events are emitted from handler {handlerChain} even though it is marked as an action that would emit Marten events. Either return the events from the handler, or use the IEventStream<T> service as an argument.");
            }
        }
    }

    private MethodCall generateLoadAggregateCode(IChain chain)
    {
        chain.Middleware.Add(new EventStoreFrame());
        var loader = typeof(LoadAggregateFrame<>).CloseAndBuildAs<MethodCall>(this, AggregateType!);


        chain.Middleware.Add(loader);
        return loader;
    }

    internal static MemberInfo DetermineVersionMember(Type aggregateType)
    {
        // The first arg doesn't matter
        var versioning =
            _versioningBaseType.CloseAndBuildAs<IAggregateVersioning>(AggregationScope.SingleStream, aggregateType);
        return versioning.VersionMember;
    }

    internal Type DetermineAggregateType(IChain chain)
    {
        if (AggregateType != null)
        {
            return AggregateType;
        }

        var firstCall = chain.HandlerCalls().First();
        var parameters = firstCall.Method.GetParameters();
        var stream = parameters.FirstOrDefault(x => x.ParameterType.Closes(typeof(IEventStream<>)));
        if (stream != null)
        {
            return stream.ParameterType.GetGenericArguments().Single();
        }

        if (parameters.Length >= 2 && (parameters[1].ParameterType.IsConcrete() || parameters[1].ParameterType.Closes(typeof(IEvent<>))))
        {
            return parameters[1].ParameterType;
        }

        // Assume that the handler type itself is the aggregate
        if (firstCall.HandlerType.HasAttribute<AggregateHandlerAttribute>())
        {
            return firstCall.HandlerType;
        }

        throw new InvalidOperationException(
            $"Unable to determine a Marten aggregate type for {chain}. You may need to explicitly specify the aggregate type in a {nameof(AggregateHandlerAttribute)} attribute");
    }

    internal static MemberInfo DetermineAggregateIdMember(Type aggregateType, Type commandType)
    {
        var conventionalMemberName = $"{aggregateType.Name}Id";
        var member = commandType.GetMembers().FirstOrDefault(x => x.HasAttribute<IdentityAttribute>())
                     ?? commandType.GetMembers().FirstOrDefault(x =>
                         x.Name.EqualsIgnoreCase(conventionalMemberName) || x.Name.EqualsIgnoreCase("Id"));

        if (member == null)
        {
            throw new InvalidOperationException(
                $"Unable to determine the aggregate id for aggregate type {aggregateType.FullNameInCode()} on command type {commandType.FullNameInCode()}. Either make a property or field named '{conventionalMemberName}', or decorate a member with the {typeof(IdentityAttribute).FullNameInCode()} attribute");
        }

        return member;
    }
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

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _stream = chain.FindVariable(typeof(IEventStream<T>));
        yield return _stream;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var variableName = (typeof(T).Name + "Event").ToCamelCase();

        writer.WriteComment(Description);
        writer.Write($"await foreach (var {variableName} in {_returnValue.Usage}) {_stream!.Usage}.{nameof(IEventStream<string>.AppendOne)}({variableName});");
        Next?.GenerateCode(method, writer);
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