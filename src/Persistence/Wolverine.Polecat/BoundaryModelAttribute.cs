using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Tags;
using Polecat;
using Polecat.Events.Dcb;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Polecat.Codegen;
using Wolverine.Polecat.Persistence.Sagas;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Polecat;

/// <summary>
///     Marks a parameter to a Wolverine message handler or HTTP endpoint method as being part of the
///     Polecat Dynamic Consistency Boundary (DCB) workflow.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class BoundaryModelAttribute : WolverineParameterAttribute, IDataRequirement, IRefersToAggregate
{
    private OnMissing? _onMissing;

    public bool Required { get; set; }
    public string MissingMessage { get; set; }

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        _onMissing ??= container.GetInstance<WolverineOptions>().EntityDefaults.OnMissing;

        var aggregateType = parameter.ParameterType;
        if (aggregateType.IsNullable())
        {
            aggregateType = aggregateType.GetInnerTypeFromNullable();
        }

        var isBoundaryParameter = false;
        if (aggregateType.Closes(typeof(IEventBoundary<>)))
        {
            aggregateType = aggregateType.GetGenericArguments()[0];
            isBoundaryParameter = true;
        }

        var firstCall = chain.HandlerCalls().First();
        var handlerType = firstCall.HandlerType;
        var loadMethodNames = new[] { "Load", "LoadAsync", "Before", "BeforeAsync" };

        var loadMethod = handlerType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .FirstOrDefault(m => loadMethodNames.Contains(m.Name) &&
                                 (m.ReturnType == typeof(EventTagQuery) ||
                                  m.ReturnType == typeof(Task<EventTagQuery>) ||
                                  m.ReturnType == typeof(ValueTask<EventTagQuery>)));

        if (loadMethod == null)
        {
            throw new InvalidOperationException(
                $"[BoundaryModel] on parameter '{parameter.Name}' in {chain} requires a Load() or Before() method " +
                $"that returns an EventTagQuery to define the tag query for FetchForWritingByTags<{aggregateType.Name}>().");
        }

        new PolecatPersistenceFrameProvider().ApplyTransactionSupport(chain, container);

        var loader = new LoadBoundaryFrame(aggregateType);
        chain.Middleware.Add(loader);

        var boundary = loader.Boundary;

        DetermineEventCaptureHandling(chain, aggregateType);

        var boundaryInterfaceType = typeof(IEventBoundary<>).MakeGenericType(aggregateType);
        Variable aggregateVariable = new MemberAccessVariable(boundary,
            boundaryInterfaceType.GetProperty(nameof(IEventBoundary<string>.Aggregate))!);

        if (Required)
        {
            var otherFrames = chain.AddStopConditionIfNull(aggregateVariable, null, this);
            var block = new LoadEntityFrameBlock(aggregateVariable, otherFrames);
            block.AlsoMirrorAsTheCreator(boundary);
            chain.Middleware.Add(block);
            aggregateVariable = block.Mirror;
        }

        if (isBoundaryParameter)
        {
            return boundary;
        }

        if (parameter.ParameterType == aggregateType || parameter.ParameterType.IsNullable() &&
            parameter.ParameterType.GetInnerTypeFromNullable() == aggregateType)
        {
            firstCall.TrySetArgument(parameter.Name, aggregateVariable);
        }

        AggregateHandling.StoreDeferredMiddlewareVariable(chain, parameter.Name, aggregateVariable);

        foreach (var methodCall in chain.Middleware.OfType<MethodCall>())
        {
            if (!methodCall.TrySetArgument(parameter.Name, aggregateVariable))
            {
                methodCall.TrySetArgument(aggregateVariable);
            }
        }

        chain.Tags["BoundaryHandling"] = new BoundaryHandlingTag(aggregateType, boundary);

        return aggregateVariable;
    }

    internal static void DetermineEventCaptureHandling(IChain chain, Type aggregateType)
    {
        var firstCall = chain.HandlerCalls().First();

        var asyncEnumerable =
            firstCall.Creates.FirstOrDefault(x => x.VariableType == typeof(IAsyncEnumerable<object>));
        if (asyncEnumerable != null)
        {
            asyncEnumerable.UseReturnAction(_ =>
            {
                return typeof(ApplyBoundaryEventsFromAsyncEnumerableFrame<>).CloseAndBuildAs<Frame>(
                    asyncEnumerable, aggregateType);
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
                v => typeof(RegisterBoundaryEventsFrame<>)
                    .CloseAndBuildAs<MethodCall>(eventsVariable, aggregateType)
                    .WrapIfNotNull(v), "Append events via DCB boundary");
            return;
        }

        if (!firstCall.Method.GetParameters()
                .Any(x => x.ParameterType.Closes(typeof(IEventBoundary<>))))
        {
            chain.ReturnVariableActionSource = new BoundaryEventCaptureActionSource(aggregateType);
        }
    }
}

internal record BoundaryHandlingTag(Type AggregateType, Variable Boundary);
