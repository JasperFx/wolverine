using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Tags;
using Marten.Events.Dcb;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Marten;

/// <summary>
///     Marks a parameter to a Wolverine message handler or HTTP endpoint method as being part of the
///     Marten Dynamic Consistency Boundary (DCB) workflow. The handler must have a Load/Before method
///     that returns an <see cref="EventTagQuery"/>. Wolverine will call
///     <c>IDocumentSession.Events.FetchForWritingByTags&lt;T&gt;(query)</c> and project the matching
///     events into the parameter type. Return values from the handler are appended via
///     <see cref="IEventBoundary{T}.AppendOne"/>.
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

        // Validate that a Load/Before method returning EventTagQuery exists on the handler type.
        // The method itself will be added to the middleware chain by ApplyImpliedMiddlewareFromHandlers()
        // which runs after this Modify() call. The LoadBoundaryFrame resolves the EventTagQuery
        // variable lazily during FindVariables().
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

        new MartenPersistenceFrameProvider().ApplyTransactionSupport(chain, container);

        // The EventTagQuery variable will be resolved lazily from the Load method's return value
        var loader = new LoadBoundaryFrame(aggregateType);
        chain.Middleware.Add(loader);

        var boundary = loader.Boundary;

        // Set up event capture: return values from the handler get appended via the boundary
        DetermineEventCaptureHandling(chain, aggregateType);

        // Extract the aggregate from the boundary
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

        // If the parameter is IEventBoundary<T>, return the boundary itself
        if (isBoundaryParameter)
        {
            return boundary;
        }

        // Relay the aggregate to the handler
        if (parameter.ParameterType == aggregateType || parameter.ParameterType.IsNullable() &&
            parameter.ParameterType.GetInnerTypeFromNullable() == aggregateType)
        {
            firstCall.TrySetArgument(parameter.Name, aggregateVariable);
        }

        // Store deferred assignment for middleware methods (Before/After)
        AggregateHandling.StoreDeferredMiddlewareVariable(chain, parameter.Name, aggregateVariable);

        // Also do immediate relay for any middleware already present
        foreach (var methodCall in chain.Middleware.OfType<MethodCall>())
        {
            if (!methodCall.TrySetArgument(parameter.Name, aggregateVariable))
            {
                methodCall.TrySetArgument(aggregateVariable);
            }
        }

        // Store boundary handling info in chain tags for reference
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

        // If there's no IEventBoundary<T> parameter, assume return values are events
        if (!firstCall.Method.GetParameters()
                .Any(x => x.ParameterType.Closes(typeof(IEventBoundary<>))))
        {
            chain.ReturnVariableActionSource = new BoundaryEventCaptureActionSource(aggregateType);
        }
    }
}

internal record BoundaryHandlingTag(Type AggregateType, Variable Boundary);
