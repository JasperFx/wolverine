using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using Marten.Schema;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Publishing;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Marten;

/// <summary>
/// Tells Wolverine handlers that this value contains a
/// list of events to be appended to the current stream
/// </summary>
public class Events : List<object>
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
public class MartenCommandWorkflowAttribute : ModifyChainAttribute
{
    private static readonly Type _versioningBaseType =
        typeof(IAggregateVersioning).Assembly.DefinedTypes.Single(x => x.Name.StartsWith("AggregateVersioning"));

    public MartenCommandWorkflowAttribute(ConcurrencyStyle loadStyle)
    {
        LoadStyle = loadStyle;
    }

    public MartenCommandWorkflowAttribute() : this(ConcurrencyStyle.Optimistic)
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

    public override void Modify(IChain chain, GenerationRules rules, IContainer container)
    {
        if (chain.Tags.ContainsKey(nameof(MartenCommandWorkflowAttribute))) return;
        chain.Tags.Add(nameof(MartenCommandWorkflowAttribute),"true");

        var handlerChain = (HandlerChain)chain;
        CommandType = handlerChain.MessageType;
        AggregateType ??= DetermineAggregateType(chain);
        AggregateIdMember = DetermineAggregateIdMember(AggregateType, CommandType);
        VersionMember = DetermineVersionMember(CommandType);

        var sessionCreator = MethodCall.For<OutboxedSessionFactory>(x => x.OpenSession(null!));
        chain.Middleware.Add(sessionCreator);

        var firstCall = handlerChain.Handlers.First();

        var loader = generateLoadAggregateCode(chain);
        if (AggregateType == firstCall.HandlerType)
        {
            chain.Middleware.Add(new MissingAggregateCheckFrame(AggregateType, CommandType, AggregateIdMember,
                loader.ReturnVariable!));
        }

        // Use the active document session as an IQuerySession instead of creating a new one
        firstCall.TrySetArgument(new Variable(typeof(IQuerySession), sessionCreator.ReturnVariable!.Usage));

        var eventsVariable = firstCall.Creates.FirstOrDefault(x => x.VariableType == typeof(Events)) ?? firstCall.Creates.FirstOrDefault(x => x.VariableType.CanBeCastTo<IEnumerable<object>>() && x.VariableType != typeof(OutgoingMessages));
        if (eventsVariable != null)
        {
            var action = eventsVariable.UseReturnAction(
                v => typeof(RegisterEventsFrame<>).CloseAndBuildAs<MethodCall>(eventsVariable, AggregateType!)
                    .WrapIfNotNull(v), "Append events to the Marten event stream");
        }

        validateMethodSignatureForEmittedEvents(chain, firstCall, handlerChain);
        relayAggregateToHandlerMethod(loader, firstCall);

        handlerChain.Postprocessors.Add(MethodCall.For<IDocumentSession>(x => x.SaveChangesAsync(default)));
    }

    private void relayAggregateToHandlerMethod(MethodCall loader, MethodCall firstCall)
    {
        var aggregateVariable = new Variable(AggregateType,
            $"{loader.ReturnVariable.Usage}.{nameof(IEventStream<string>.Aggregate)}");

        if (firstCall.HandlerType == AggregateType)
        {
            // If the handle method is on the aggregate itself
            firstCall.Target = aggregateVariable;
        }
        else
        {
            firstCall.TrySetArgument(aggregateVariable);
        }
    }

    private static void validateMethodSignatureForEmittedEvents(IChain chain, MethodCall firstCall,
        HandlerChain handlerChain)
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

    internal MemberInfo DetermineVersionMember(Type aggregateType)
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

        if (parameters.Length >= 2 && parameters[1].ParameterType.IsConcrete())
        {
            return parameters[1].ParameterType;
        }

        // Assume that the handler type itself is the aggregate
        if (firstCall.HandlerType.HasAttribute<MartenCommandWorkflowAttribute>())
        {
            return firstCall.HandlerType;
        }

        throw new InvalidOperationException(
            $"Unable to determine a Marten aggregate type for {chain}. You may need to explicitly specify the aggregate type in a {nameof(MartenCommandWorkflowAttribute)} attribute");
    }

    internal static MemberInfo DetermineAggregateIdMember(Type aggregateType, Type commandType)
    {
        var conventionalMemberName = $"{aggregateType.Name}Id";
        var member = commandType.GetMembers().FirstOrDefault(x =>
            x.HasAttribute<IdentityAttribute>() || x.Name.EqualsIgnoreCase(conventionalMemberName));

        if (member == null)
        {
            throw new InvalidOperationException(
                $"Unable to determine the aggregate id for aggregate type {aggregateType.FullNameInCode()} on command type {commandType.FullNameInCode()}. Either make a property or field named '{conventionalMemberName}', or decorate a member with the {typeof(IdentityAttribute).FullNameInCode()} attribute");
        }

        return member;
    }
}