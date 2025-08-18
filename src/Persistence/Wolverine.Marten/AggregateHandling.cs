using System.Diagnostics;
using System.Reflection;
using ImTools;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using Marten;
using Marten.Events;
using Marten.Schema;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Marten;

internal record AggregateHandling(IDataRequirement Requirement)
{
    private static readonly Type _versioningBaseType = typeof(AggregateVersioning<>);

    public Type AggregateType { get; init; }
    public Variable AggregateId { get; init; }

    public ConcurrencyStyle LoadStyle { get; init; }
    public Variable? Version { get; init; }
    public ParameterInfo? Parameter { get; set; }

    public Variable Apply(IChain chain, IServiceContainer container)
    {
        Store(chain);

        new MartenPersistenceFrameProvider().ApplyTransactionSupport(chain, container);

        var loader = GenerateLoadAggregateCode(chain);
        var firstCall = chain.HandlerCalls().First();

        var eventStream = loader.ReturnVariable!;
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
        chain.Tags[nameof(AggregateHandling)] = this;
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

    public MethodCall GenerateLoadAggregateCode(IChain chain)
    {
        if (!chain.Middleware.OfType<EventStoreFrame>().Any())
        {
            chain.Middleware.Add(new EventStoreFrame());
        }

        var loader = typeof(LoadAggregateFrame<>).CloseAndBuildAs<MethodCall>(this, AggregateType!);

        chain.Middleware.Add(loader);
        return loader;
    }

    internal static (MemberInfo, MemberInfo?) DetermineAggregateIdAndVersion(Type aggregateType, Type commandType,
        IServiceContainer container)
    {
        if (commandType.Closes(typeof(IEvent<>)))
        {
            var concreteEventType = typeof(Event<>).MakeGenericType(commandType.GetGenericArguments()[0]);

            // This CANNOT work if you capture the version, because there's no way to know if the aggregate version
            // has advanced
            //VersionMember = concreteEventType.GetProperty(nameof(IEvent.Version));

            var options = container.Services.GetRequiredService<StoreOptions>();
            var flattenHierarchy = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var member = options.Events.StreamIdentity == StreamIdentity.AsGuid
                ? concreteEventType.GetProperty(nameof(IEvent.StreamId), flattenHierarchy)
                : concreteEventType.GetProperty(nameof(IEvent.StreamKey), flattenHierarchy);

            return (member!, null);
        }

        var aggregateId = DetermineAggregateIdMember(aggregateType, commandType);
        var version = DetermineVersionMember(commandType);
        return (aggregateId, version);
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

    internal Variable RelayAggregateToHandlerMethod(Variable eventStream, IChain chain, MethodCall firstCall,
        Type aggregateType)
    {
        Variable aggregateVariable = new MemberAccessVariable(eventStream,
            typeof(IEventStream<>).MakeGenericType(aggregateType).GetProperty(nameof(IEventStream<string>.Aggregate)));

        if (Requirement.Required)
        {
            var otherFrames = chain.AddStopConditionIfNull(aggregateVariable, AggregateId, Requirement);
            
            var block = new LoadEntityFrameBlock(aggregateVariable, otherFrames);
            chain.Middleware.Add(block);

            aggregateVariable = block.Mirror;
        }

        if (firstCall.HandlerType == aggregateType)
        {
            // If the handle method is on the aggregate itself
            firstCall.Target = aggregateVariable;
        }
        else
        {
            if (!firstCall.TrySetArgument(aggregateVariable))
            {
                if (Parameter != null && Parameter.ParameterType.Closes(typeof(IEventStream<>)))
                {
                    var index = firstCall.Method.GetParameters().IndexOf(x => x.Name == Parameter.Name);
                    firstCall.Arguments[index] = eventStream;
                }
            };
        }

        foreach (var methodCall in chain.Middleware.OfType<MethodCall>())
        {
            methodCall.TrySetArgument(aggregateVariable);
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

        // Assume that the handler type itself is the aggregate
        if (firstCall.HandlerType.HasAttribute<AggregateHandlerAttribute>())
        {
            return firstCall.HandlerType;
        }

        throw new InvalidOperationException(
            $"Unable to determine a Marten aggregate type for {chain}. You may need to explicitly specify the aggregate type in a {nameof(AggregateHandlerAttribute)} attribute");
    }


    internal static MemberInfo DetermineVersionMember(Type aggregateType)
    {
        // The first arg doesn't matter
        var versioning =
            _versioningBaseType.CloseAndBuildAs<IAggregateVersioning>(AggregationScope.SingleStream, aggregateType);
        return versioning.VersionMember;
    }
}