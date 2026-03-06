using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Polecat.Events;
using Wolverine.Configuration;
using Wolverine.Polecat.Codegen;
using Wolverine.Polecat.Persistence.Sagas;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Polecat;

internal record AggregateHandling(IDataRequirement Requirement)
{
    private static readonly Type _versioningBaseType = typeof(AggregateVersioning<>);

    public Type AggregateType { get; init; }
    public Variable AggregateId { get; init; }

    public ConcurrencyStyle LoadStyle { get; init; }
    public Variable? Version { get; init; }
    public bool AlwaysEnforceConsistency { get; init; }
    public ParameterInfo? Parameter { get; set; }
    public bool IsNaturalKey { get; init; }

    public Variable Apply(IChain chain, IServiceContainer container)
    {
        Store(chain);

        new PolecatPersistenceFrameProvider().ApplyTransactionSupport(chain, container);

        var loader = new LoadAggregateFrame(this);
        chain.Middleware.Add(loader);

        var firstCall = chain.HandlerCalls().First();

        var eventStream = loader.Stream!;
        if (Parameter != null)
        {
            eventStream.OverrideName("stream_" + Parameter.Name);
        }

        if (AggregateType == firstCall.HandlerType)
        {
            chain.Middleware.Add(new MissingAggregateCheckFrame(AggregateType, AggregateId,
                eventStream));
        }

        DetermineEventCaptureHandling(chain, firstCall, AggregateType);

        ValidateMethodSignatureForEmittedEvents(chain, firstCall, chain);
        var aggregate = RelayAggregateToHandlerMethod(eventStream, chain, firstCall, AggregateType);

        if (Parameter != null && Parameter.ParameterType.Closes(typeof(IEventStream<>)))
        {
            return eventStream;
        }

        return aggregate;
    }

    public void Store(IChain chain)
    {
        if (chain.Tags.TryGetValue(nameof(AggregateHandling), out var raw))
        {
            if (raw is AggregateHandling handling)
            {
                if (ReferenceEquals(handling, this)) return;
                chain.Tags[nameof(AggregateHandling)] = new List<AggregateHandling> { handling, this };
            }
            else if (raw is List<AggregateHandling> list)
            {
                list.Add(this);
            }
        }
        else
        {
            chain.Tags[nameof(AggregateHandling)] = this;
        }
    }

    public static bool TryLoad(IChain chain, out AggregateHandling handling)
    {
        if (chain.Tags.TryGetValue(nameof(AggregateHandling), out var raw))
        {
            if (raw is AggregateHandling h)
            {
                handling = h;
                return true;
            }
        }

        handling = default;
        return false;
    }

    public static bool TryLoad<T>(IChain chain, out AggregateHandling handling)
    {
        if (chain.Tags.TryGetValue(nameof(AggregateHandling), out var raw))
        {
            if (raw is AggregateHandling h && h.AggregateType == typeof(T))
            {
                handling = h;
                return true;
            }

            if (raw is List<AggregateHandling> list)
            {
                handling = list.FirstOrDefault(x => x.AggregateType == typeof(T));
                return handling != null;
            }
        }

        handling = default;
        return false;
    }

    internal static (MemberInfo, MemberInfo?) DetermineAggregateIdAndVersion(Type aggregateType, Type commandType,
        IServiceContainer container, string? versionSource = null)
    {
        if (commandType.Closes(typeof(IEvent<>)))
        {
            var concreteEventType = typeof(Event<>).MakeGenericType(commandType.GetGenericArguments()[0]);

            var options = container.Services.GetRequiredService<StoreOptions>();
            var flattenHierarchy = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var member = options.Events.StreamIdentity == StreamIdentity.AsGuid
                ? concreteEventType.GetProperty(nameof(IEvent.StreamId), flattenHierarchy)
                : concreteEventType.GetProperty(nameof(IEvent.StreamKey), flattenHierarchy);

            return (member!, null);
        }

        var aggregateId = DetermineAggregateIdMember(aggregateType, commandType);
        var version = versionSource != null
            ? DetermineVersionMemberByName(commandType, versionSource)
            : DetermineVersionMember(commandType);
        return (aggregateId, version);
    }

    internal static MemberInfo? DetermineVersionMemberByName(Type commandType, string memberName)
    {
        var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var prop = commandType.GetProperties(bindingFlags)
            .FirstOrDefault(x => x.Name.EqualsIgnoreCase(memberName)
                                 && (x.PropertyType == typeof(int) || x.PropertyType == typeof(long)));

        if (prop != null) return prop;

        var field = commandType.GetFields(bindingFlags)
            .FirstOrDefault(x => x.Name.EqualsIgnoreCase(memberName)
                                 && (x.FieldType == typeof(int) || x.FieldType == typeof(long)));

        return field;
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
                    $"No events are emitted from handler {handlerChain} even though it is marked as an action that would emit Polecat events. Either return the events from the handler, or use the IEventStream<T> service as an argument.");
            }
        }
    }

    internal static MemberInfo DetermineAggregateIdMember(Type aggregateType, Type commandType)
    {
        var conventionalMemberName = $"{aggregateType.Name}Id";
        var member = commandType.GetMembers().FirstOrDefault(x => x.HasAttribute<IdentityAttribute>())
                     ?? commandType.GetMembers().FirstOrDefault(x =>
                         x.Name.EqualsIgnoreCase(conventionalMemberName) || x.Name.EqualsIgnoreCase("Id"));

        if (member == null)
        {
            member = TryFindStrongTypedIdMember(aggregateType, commandType);
        }

        if (member == null)
        {
            throw new InvalidOperationException(
                $"Unable to determine the aggregate id for aggregate type {aggregateType.FullNameInCode()} on command type {commandType.FullNameInCode()}. Either make a property or field named '{conventionalMemberName}', or decorate a member with the {typeof(IdentityAttribute).FullNameInCode()} attribute");
        }

        return member;
    }

    internal static MemberInfo? TryFindStrongTypedIdMember(Type aggregateType, Type commandType)
    {
        var strongTypedIdType = WriteAggregateAttribute.FindIdentifiedByType(aggregateType);

        if (strongTypedIdType == null)
        {
            var idProp = aggregateType.GetProperty("Id");
            if (idProp != null && !WriteAggregateAttribute.IsPrimitiveIdType(idProp.PropertyType))
            {
                strongTypedIdType = idProp.PropertyType;
            }
        }

        if (strongTypedIdType == null) return null;

        var matchingProps = commandType.GetProperties()
            .Where(x => x.PropertyType == strongTypedIdType && x.CanRead)
            .ToArray();

        return matchingProps.Length == 1 ? matchingProps[0] : null;
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
                    .WrapIfNotNull(v), "Append events to the Polecat event stream");
            return;
        }

        if (!firstCall.Method.GetParameters().Any(x => x.ParameterType.Closes(typeof(IEventStream<>))))
        {
            chain.ReturnVariableActionSource = new EventCaptureActionSource(aggregateType);
        }
    }

    internal Variable RelayAggregateToHandlerMethod(Variable eventStream, IChain chain, MethodCall firstCall,
        Type aggregateType)
    {
        Variable aggregateVariable = new MemberAccessVariable(eventStream,
            typeof(IEventStream<>).MakeGenericType(aggregateType).GetProperty(nameof(IEventStream<string>.Aggregate)));

        if (Requirement.Required)
        {
            var otherFrames = chain.AddStopConditionIfNull(aggregateVariable, AggregateId, Requirement);
            var block = new LoadEntityFrameBlock(aggregateVariable, otherFrames);
            block.AlsoMirrorAsTheCreator(eventStream);
            chain.Middleware.Add(block);
            aggregateVariable = block.Mirror;
        }

        if (firstCall.HandlerType == aggregateType)
        {
            firstCall.Target = aggregateVariable;
        }
        else if (Parameter != null && Parameter.ParameterType.Closes(typeof(IEventStream<>)))
        {
            var index = Array.FindIndex(firstCall.Method.GetParameters(), x => x.Name == Parameter.Name);
            firstCall.Arguments[index] = eventStream;
        }
        else if (Parameter != null)
        {
            firstCall.TrySetArgument(Parameter.Name, aggregateVariable);
        }
        else
        {
            firstCall.TrySetArgument(aggregateVariable);
        }

        if (Parameter != null)
        {
            StoreDeferredMiddlewareVariable(chain, Parameter.Name, aggregateVariable);
        }

        foreach (var methodCall in chain.Middleware.OfType<MethodCall>())
        {
            if (Parameter != null)
            {
                if (!methodCall.TrySetArgument(Parameter.Name, aggregateVariable))
                {
                    methodCall.TrySetArgument(aggregateVariable);
                }
            }
            else
            {
                methodCall.TrySetArgument(aggregateVariable);
            }
        }

        return aggregateVariable;
    }

    internal static Type DetermineAggregateType(IChain chain)
    {
        var firstCall = chain.HandlerCalls().First();
        var parameters = firstCall.Method.GetParameters();
        var stream = parameters.FirstOrDefault(x => x.ParameterType.Closes(typeof(IEventStream<>)));
        if (stream != null)
        {
            return stream.ParameterType.GetGenericArguments().Single();
        }

        if (parameters.Length >= 2 && (parameters[1].ParameterType.IsConcrete() ||
                                       parameters[1].ParameterType.Closes(typeof(IEvent<>))))
        {
            return parameters[1].ParameterType;
        }

        if (firstCall.HandlerType.HasAttribute<AggregateHandlerAttribute>())
        {
            return firstCall.HandlerType;
        }

        throw new InvalidOperationException(
            $"Unable to determine a Polecat aggregate type for {chain}. You may need to explicitly specify the aggregate type in a {nameof(AggregateHandlerAttribute)} attribute");
    }

    internal static MemberInfo DetermineVersionMember(Type aggregateType)
    {
        var versioning =
            _versioningBaseType.CloseAndBuildAs<IAggregateVersioning>(AggregationScope.SingleStream, aggregateType);
        return versioning.VersionMember;
    }

    internal static void StoreDeferredMiddlewareVariable(IChain chain, string parameterName, Variable variable)
    {
        const string key = "DeferredMiddlewareVariables";
        if (!chain.Tags.TryGetValue(key, out var raw))
        {
            raw = new List<(string Name, Variable Variable)>();
            chain.Tags[key] = raw;
        }
        ((List<(string Name, Variable Variable)>)raw).Add((parameterName, variable));
    }
}

internal class ApplyEventsFromAsyncEnumerableFrame<T> : AsyncFrame, IReturnVariableAction where T : class
{
    private readonly Variable _returnValue;
    private Variable? _stream;

    public ApplyEventsFromAsyncEnumerableFrame(Variable returnValue)
    {
        _returnValue = returnValue;
        uses.Add(_returnValue);
    }

    public string Description => "Apply events to Polecat event stream";

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
