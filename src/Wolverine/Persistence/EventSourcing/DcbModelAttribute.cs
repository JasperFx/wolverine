using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Tags;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Marks a parameter to a Wolverine message handler or HTTP endpoint method as a model built by a
///     <b>Dynamic Consistency Boundary</b> (DCB) — a model projected from every stream whose events match
///     a tag query, rather than from one stream.
/// </summary>
/// <remarks>
///     <para>
///     The handler must have a <c>Load()</c> / <c>LoadAsync()</c> / <c>Before()</c> / <c>BeforeAsync()</c>
///     method returning an <see cref="EventTagQuery" />. Wolverine calls the store's
///     <c>FetchForWritingByTags&lt;T&gt;(query)</c> with it and projects the matching events into the
///     parameter type. Return values from the handler are appended via
///     <see cref="IEventBoundary{T}.AppendOne" />.
///     </para>
///     <para>
///     Store-agnostic — it works against whichever event store integration claims the model type, so the
///     same handler signature is valid on Marten, on Polecat, or on any integration whose
///     <see cref="IEventSourcingFrameProvider" /> implements
///     <see cref="IEventSourcingFrameProvider.BuildLoadBoundaryFrame" />.
///     </para>
///     <para>
///     <c>Wolverine.Marten.BoundaryModelAttribute</c> and <c>Wolverine.Polecat.BoundaryModelAttribute</c>
///     both derive from this and are kept as-is for existing code. They were 190-line files differing
///     only in comments. GH-3911.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class DcbModelAttribute : WolverineParameterAttribute, IDataRequirement, IRefersToAggregate
{
    private static readonly string[] _loadMethodNames = ["Load", "LoadAsync", "Before", "BeforeAsync"];

    private OnMissing? _onMissing;

    public bool Required { get; set; }
    public string MissingMessage { get; set; } = null!;

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2062",
        Justification = "modelType originates from parameter.ParameterType; AOT consumers preserve it via DynamicDependency / source-generator registration.")]
    [UnconditionalSuppressMessage("Trimming", "IL2065",
        Justification = "MakeGenericType closes IEventBoundary<TModel>; GetProperty(nameof(IEventBoundary.Aggregate)) is statically referenced via nameof and the closed-generic IEventBoundary<TModel> preserves the Aggregate property by virtue of being instantiated by codegen. AOT consumers pre-generate via TypeLoadMode.Static.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "The handler type comes from handler discovery, which already roots it; this is the dynamic codegen path. See docs/guide/aot.md.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Closes() only tests whether the parameter type closes IEventBoundary<>; it reads interfaces off a type already rooted by handler discovery.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType closes IEventBoundary<TModel> at codegen time; AOT consumers pre-generate via TypeLoadMode.Static.")]
    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        _onMissing ??= container.GetInstance<WolverineOptions>().EntityDefaults.OnMissing;

        var modelType = parameter.ParameterType;
        if (modelType.IsNullable())
        {
            modelType = modelType.GetInnerTypeFromNullable();
        }

        var isBoundaryParameter = false;
        if (modelType.Closes(typeof(IEventBoundary<>)))
        {
            modelType = modelType.GetGenericArguments()[0];
            isBoundaryParameter = true;
        }

        // GH-3911: which store owns this model is resolved out of the persistence strategies already
        // registered on these GenerationRules - the same registry [WriteModel] resolves through - so
        // nothing here names a store.
        var provider = ResolveEventSourcingProvider(rules, container, modelType);

        // Validate that a Load/Before method returning EventTagQuery exists on the handler type.
        // The method itself will be added to the middleware chain by ApplyImpliedMiddlewareFromHandlers()
        // which runs after this Modify() call. The store's load frame resolves the EventTagQuery
        // variable lazily during FindVariables().
        var firstCall = chain.HandlerCalls().First();
        assertTagQueryLoadMethodExists(chain, parameter, firstCall.HandlerType, modelType);


        // The seam is implemented on the store's IPersistenceFrameProvider, so this reaches the right
        // store's transaction middleware without core knowing which store that is.
        ((IPersistenceFrameProvider)provider).ApplyTransactionSupport(chain, container);
        chain.IsTransactional = true;

        // One fetch per (chain, model type). A second [DcbModel] of the same type (e.g. on Validate plus
        // Handle) reuses the same boundary, otherwise both emit identically-named "var" declarations ->
        // CS0128. Matching on the created variable rather than on a frame type is what lets each store
        // keep its own load frame private to itself.
        var boundaryType = typeof(IEventBoundary<>).MakeGenericType(modelType);
        var boundary = chain.Middleware.SelectMany(x => x.Creates)
            .FirstOrDefault(x => x.VariableType == boundaryType);

        if (boundary == null)
        {
            var loader = provider.BuildLoadBoundaryFrame(modelType);
            chain.Middleware.Add(loader);

            boundary = loader.Creates.FirstOrDefault(x => x.VariableType == boundaryType)
                       ?? throw new InvalidOperationException(
                           $"The {provider.StoreName} integration's {loader.GetType().FullNameInCode()} did not create a variable of type " +
                           $"{boundaryType.FullNameInCode()}. {nameof(IEventSourcingFrameProvider)}.{nameof(IEventSourcingFrameProvider.BuildLoadBoundaryFrame)} " +
                           "must return a frame that creates exactly one event boundary variable for the model type.");
        }

        // Set up event capture: return values from the handler get appended via the boundary
        DetermineEventCaptureHandling(chain, modelType, provider);

        // Extract the model from the boundary
        Variable modelVariable = new MemberAccessVariable(boundary,
            boundaryType.GetProperty(nameof(IEventBoundary<string>.Aggregate))!);

        if (chain.IsDataRequired(this))
        {
            var otherFrames = chain.AddStopConditionIfNull(modelVariable, null, this);
            var block = new LoadEntityFrameBlock(modelVariable, otherFrames);
            block.AlsoMirrorAsTheCreator(boundary);
            chain.Middleware.Add(block);
            modelVariable = block.Mirror;
        }

        // If the parameter is IEventBoundary<T>, return the boundary itself
        if (isBoundaryParameter)
        {
            return boundary;
        }

        // Relay the model to the handler
        if (parameter.ParameterType == modelType || parameter.ParameterType.IsNullable() &&
            parameter.ParameterType.GetInnerTypeFromNullable() == modelType)
        {
            firstCall.TrySetArgument(parameter.Name!, modelVariable);
        }

        // Store deferred assignment for middleware methods (Before/After)
        AggregateHandling.StoreDeferredMiddlewareVariable(chain, parameter.Name!, modelVariable);

        // Also do immediate relay for any middleware already present
        foreach (var methodCall in chain.Middleware.OfType<MethodCall>())
        {
            if (!methodCall.TrySetArgument(parameter.Name!, modelVariable))
            {
                methodCall.TrySetArgument(modelVariable);
            }
        }

        // Store boundary handling info in chain tags for reference
        chain.Tags["BoundaryHandling"] = new BoundaryHandlingTag(modelType, boundary);

        return modelVariable;
    }

    /// <summary>
    ///     The event store integration that owns <paramref name="modelType" />.
    /// </summary>
    /// <remarks>
    ///     The default resolves it out of the persistence strategies registered on
    ///     <see cref="GenerationRules" />, which is what makes this attribute store-agnostic. A store's own
    ///     attribute overrides this to name itself instead, so that <c>AddMarten(...)</c> without
    ///     <c>IntegrateWithWolverine()</c> — where nothing registers a persistence strategy — keeps working
    ///     the way it did before GH-3911. Same reasoning as
    ///     <see cref="WriteModelAttribute.ResolveEventSourcingProvider" />.
    /// </remarks>
    protected virtual IEventSourcingFrameProvider ResolveEventSourcingProvider(GenerationRules rules,
        IServiceContainer container, Type modelType)
    {
        return rules.FindEventSourcingFrameProvider(container, modelType);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "The handler type comes from handler discovery, which already roots it; this is the dynamic codegen path. See docs/guide/aot.md.")]
    private void assertTagQueryLoadMethodExists(IChain chain, ParameterInfo parameter, Type handlerType,
        Type modelType)
    {
        var loadMethod = handlerType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .FirstOrDefault(m => _loadMethodNames.Contains(m.Name) &&
                                 (m.ReturnType == typeof(EventTagQuery) ||
                                  m.ReturnType == typeof(Task<EventTagQuery>) ||
                                  m.ReturnType == typeof(ValueTask<EventTagQuery>)));

        if (loadMethod == null)
        {
            // Name the attribute the user actually wrote. A Marten or Polecat handler says
            // [BoundaryModel], and telling it about [DcbModel] would send the author looking for
            // something that is not in their file.
            var name = GetType().Name;
            if (name.EndsWith("Attribute", StringComparison.Ordinal))
            {
                name = name[..^"Attribute".Length];
            }

            throw new InvalidOperationException(
                $"[{name}] on parameter '{parameter.Name}' in {chain} requires a Load() or Before() method " +
                $"that returns an EventTagQuery to define the tag query for FetchForWritingByTags<{modelType.Name}>().");
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "CloseAndBuildAs closes ApplyBoundaryEventsFromAsyncEnumerableFrame<>/RegisterBoundaryEventsFrame<> over the model type at codegen time. AOT consumers pre-generate via TypeLoadMode.Static.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "CloseAndBuildAs uses MakeGenericType at codegen time only. AOT consumers pre-generate via TypeLoadMode.Static so the reflective close never fires at runtime.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Closes() only tests whether a handler parameter type closes IEventBoundary<>; it reads interfaces off a type already rooted by handler discovery.")]
    internal static void DetermineEventCaptureHandling(IChain chain, Type modelType,
        IEventSourcingFrameProvider provider)
    {
        var firstCall = chain.HandlerCalls().First();

        var asyncEnumerable =
            firstCall.Creates.FirstOrDefault(x => x.VariableType == typeof(IAsyncEnumerable<object>));
        if (asyncEnumerable != null)
        {
            asyncEnumerable.UseReturnAction(_ =>
            {
                return typeof(ApplyBoundaryEventsFromAsyncEnumerableFrame<>).CloseAndBuildAs<Frame>(
                    asyncEnumerable, modelType);
            });
            return;
        }

        var eventsVariable = firstCall.Creates.FirstOrDefault(x => x.VariableType == provider.EventsCollectionType) ??
                             firstCall.Creates.FirstOrDefault(x =>
                                 x.VariableType.CanBeCastTo<IEnumerable<object>>() &&
                                 !x.VariableType.CanBeCastTo<IWolverineReturnType>());

        if (eventsVariable != null)
        {
            eventsVariable.UseReturnAction(
                v => typeof(RegisterBoundaryEventsFrame<>)
                    .CloseAndBuildAs<MethodCall>(eventsVariable, modelType)
                    .WrapIfNotNull(v), "Append events via DCB boundary");
            return;
        }

        // If there's no IEventBoundary<T> parameter, assume return values are events
        if (!firstCall.Method.GetParameters()
                .Any(x => x.ParameterType.Closes(typeof(IEventBoundary<>))))
        {
            chain.ReturnVariableActionSource = new BoundaryEventCaptureActionSource(modelType);
        }
    }
}

internal record BoundaryHandlingTag(Type AggregateType, Variable Boundary);
