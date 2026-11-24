using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events.Aggregation;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Marks a parameter to a Wolverine HTTP endpoint or message handler method as an event sourced model
///     that the handler only <b>reads</b>: the aggregate is resolved through the store's
///     <c>FetchLatest()</c> API, with no stream lock and no expectation that the handler appends events.
/// </summary>
/// <remarks>
///     <para>
///     Store-agnostic — it works against whichever event store integration claims the aggregate type. Use
///     <see cref="WriteModelAttribute" /> instead when the handler is going to emit events.
///     </para>
///     <para>
///     <c>Wolverine.Marten.ReadAggregateAttribute</c> and <c>Wolverine.Polecat.ReadAggregateAttribute</c>
///     both derive from this and are kept as-is for existing code. GH-3907.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class ReadModelAttribute : WolverineParameterAttribute, IDataRequirement, IRefersToAggregate
{
    private OnMissing? _onMissing;
    private bool? _required;

    public ReadModelAttribute()
    {
        ValueSource = ValueSource.Anything;
    }

    public ReadModelAttribute(string argumentName) : base(argumentName)
    {
        ValueSource = ValueSource.Anything;
    }

    /// <summary>
    ///     Should Wolverine stop the handler when the model cannot be found? Defaults to <b>the opposite of
    ///     the parameter's nullable annotation</b>: <c>Order order</c> is required, <c>Order? order</c> is not.
    /// </summary>
    /// <remarks>
    ///     GH-3929, matching what GH-3916 did for <see cref="WriteModelAttribute" />. Before this the
    ///     default was an unconditional <c>true</c>, so a parameter annotated nullable — the author saying
    ///     "I will handle absence" — still generated an <c>EntityIsNotNullGuard</c> and a
    ///     <c>HandlerContinuation.Stop</c>, making the handler's own null branch dead code. An explicit
    ///     <c>Required</c> at the call site still wins over the annotation.
    ///     <para>
    ///     <c>[ReadAggregate]</c> keeps the old unconditional default — see <see cref="DefaultRequired" />.
    ///     </para>
    /// </remarks>
    public bool Required
    {
        get => _required ?? true;
        set => _required = value;
    }

    /// <summary>
    ///     The value <see cref="Required" /> takes when the call site did not set it. Defaults to the
    ///     opposite of the parameter's nullable annotation.
    /// </summary>
    /// <remarks>
    ///     GH-3929: virtual so that a store's own spelling of this workflow can keep the default it
    ///     shipped with. <c>[ReadAggregate]</c> predates the nullability inference and overrides this to
    ///     an unconditional <c>true</c>, because changing the default under existing code is a silent
    ///     behaviour change that only shows up at runtime.
    /// </remarks>
    protected virtual bool DefaultRequired(ParameterInfo parameter)
    {
        return !ParameterNullability.IsNullableAnnotated(parameter);
    }

    public string MissingMessage { get; set; } = null!;

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }

    /// <summary>
    ///     Read the model by this natural key type rather than by its stream id. Found the same way as for
    ///     <see cref="WriteModelAttribute.NaturalKey" />. <c>[ReadModel&lt;TKey&gt;]</c> and
    ///     <c>[ReadAggregate&lt;TKey&gt;]</c> set this.
    /// </summary>
    public Type? NaturalKey
    {
        get => _naturalKey ?? NaturalKeyTypeArgument;
        set => _naturalKey = value;
    }

    private Type? _naturalKey;

    /// <summary>
    ///     The natural key type a generic spelling such as <c>[ReadModel&lt;TKey&gt;]</c> names as its type argument.
    ///     Kept apart from <see cref="NaturalKey" /> so that naming two different key types is refused rather than
    ///     settled silently in favour of <see cref="NaturalKey" />. GH-5947.
    /// </summary>
    protected Type? NaturalKeyTypeArgument { get; set; }

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        _onMissing ??= container.GetInstance<WolverineOptions>().EntityDefaults.OnMissing;

        // GH-3929: only when the call site said nothing. An explicit [ReadModel(Required = true)] on a
        // nullable parameter still gets its guard - loudly wrong beats silently overridden.
        _required ??= DefaultRequired(parameter);

        var provider = ResolveEventSourcingProvider(rules, container, parameter.ParameterType);

        // I know it's goofy that this refers to the saga, but it should work fine here too.
        // GH-4441: the chain goes over the seam so the identity type comes from the store this chain is
        // routed to rather than the default one.
        var idType = ((IPersistenceFrameProvider)provider).DetermineSagaIdType(parameter.ParameterType, chain,
            container);

        var spelling = NaturalKeyIdentity.SpellingOf(this);
        NaturalKeyIdentity.AssertOneKeyType(NaturalKeyTypeArgument, _naturalKey, spelling, parameter.ParameterType,
            chain);

        Frame? frame = null;
        Variable? identity;
        if (NaturalKey != null)
        {
            // Named explicitly, so the stream id is never considered
            identity = NaturalKeyIdentity.FindByKeyType(provider, parameter.ParameterType, NaturalKey, chain,
                container, ArgumentName, ValueSource);
            frame = provider.BuildFetchLatestByNaturalKeyFrame(parameter.ParameterType, identity)
                    ?? throw new InvalidOperationException(
                        $"The {provider.StoreName} integration cannot read {parameter.ParameterType.FullNameInCode()} by its natural key on {chain.HandlerCalls().First()}.");
        }
        else if (!tryFindIdentityVariable(chain, parameter, idType, out identity))
        {
            // Fall back to strong typed ID matching
            identity = tryFindStrongTypedIdentityVariable(chain, parameter.ParameterType, idType);
        }
        else
        {
            NaturalKeyIdentity.WarnOfUntypedRouteValue(provider, parameter.ParameterType, ArgumentName, chain,
                container, spelling);
        }

        if (identity == null)
        {
            // Then to one of the aggregate's natural keys, as [WriteModel] does
            var aggregateType = parameter.ParameterType;
            identity = NaturalKeyIdentity.Find(provider, aggregateType, chain, container,
                keyType => tryFindIdentityVariable(chain, parameter, keyType, out var byName)
                    ? byName
                    : tryFindStrongTypedIdentityVariable(chain, aggregateType, keyType), spelling);

            // A store that cannot fetch by natural key builds no frame, and the load is reported as before
            if (identity != null) frame = provider.BuildFetchLatestByNaturalKeyFrame(aggregateType, identity);
            if (frame == null) throw new InvalidEntityLoadUsageException(this, parameter);
        }

        // The store builds this frame: core never writes "session.Events.FetchLatest<T>(...)" itself,
        // which is what keeps Marten's batch-query enlistment on Marten's side of the seam. GH-3907.
        frame ??= provider.BuildFetchLatestFrame(parameter.ParameterType, identity!);
        var aggregate = FindAggregateVariable(frame, parameter.ParameterType, provider);
        aggregate.OverrideName(parameter.Name!);

        Variable returnVariable;
        if (chain.IsDataRequired(this))
        {
            var otherFrames = chain.AddStopConditionIfNull(aggregate, identity, this);

            var block = new LoadEntityFrameBlock(aggregate, otherFrames);
            chain.Middleware.Add(block);

            returnVariable = block.Mirror;
        }
        else
        {
            chain.Middleware.Add(frame);
            returnVariable = aggregate;
        }

        // Store deferred assignment for middleware methods added later (Before/After)
        AggregateHandling.StoreDeferredMiddlewareVariable(chain, parameter.Name!, returnVariable);

        return returnVariable;
    }

    /// <summary>
    ///     The event store integration that owns <paramref name="modelType" />.
    /// </summary>
    /// <remarks>
    ///     The default resolves it out of the persistence strategies registered on
    ///     <see cref="GenerationRules" /> — the same registry <c>[Entity]</c> resolves through — which is
    ///     what makes this attribute store-agnostic.
    ///     <para>
    ///     A store's own attribute overrides this to name itself instead. That is not just belt and
    ///     braces: <c>AddMarten(...)</c> without <c>IntegrateWithWolverine()</c> is a supported
    ///     configuration, and in it nothing ever registers a persistence strategy — yet
    ///     <c>[WriteAggregate]</c> worked there before GH-3907 because it named its provider directly.
    ///     Overriding keeps that true. GH-3907.
    ///     </para>
    /// </remarks>
    protected virtual IEventSourcingFrameProvider ResolveEventSourcingProvider(GenerationRules rules,
        IServiceContainer container, Type modelType)
    {
        return rules.FindEventSourcingFrameProvider(container, modelType);
    }

    private static Variable FindAggregateVariable(Frame frame, Type aggregateType,
        IEventSourcingFrameProvider provider)
    {
        var aggregate = frame.Creates.FirstOrDefault(x => x.VariableType == aggregateType);

        if (aggregate == null)
        {
            throw new InvalidOperationException(
                $"The {provider.StoreName} integration's {frame.GetType().FullNameInCode()} did not create a variable of type " +
                $"{aggregateType.FullNameInCode()}. {nameof(IEventSourcingFrameProvider)}.{nameof(IEventSourcingFrameProvider.BuildFetchLatestFrame)} " +
                "must return a frame that creates exactly one variable of the aggregate type.");
        }

        return aggregate;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Handler/command/model types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    private Variable? tryFindStrongTypedIdentityVariable(IChain chain, Type aggregateType, Type idType)
    {
        var strongTypedIdType = idType;

        if (WriteModelAttribute.IsPrimitiveIdType(idType))
        {
            strongTypedIdType = WriteModelAttribute.FindIdentifiedByType(aggregateType);
        }

        if (strongTypedIdType == null || WriteModelAttribute.IsPrimitiveIdType(strongTypedIdType)) return null;

        var inputType = chain.InputType();
        if (inputType == null) return null;

        var matchingProps = inputType.GetProperties()
            .Where(x => x.PropertyType == strongTypedIdType && x.CanRead)
            .ToArray();

        if (matchingProps.Length == 1)
        {
            if (chain.TryFindVariable(matchingProps[0].Name, ValueSource, strongTypedIdType, out var variable))
            {
                return variable;
            }
        }

        return null;
    }
}

/// <summary>
///     <see cref="ReadModelAttribute" /> reading the model by its <typeparamref name="TNaturalKey" /> natural
///     key. Equivalent to <c>[ReadModel(NaturalKey = typeof(TNaturalKey))]</c>.
/// </summary>
/// <typeparam name="TNaturalKey">
///     The model's own natural key type, or one it maps from another projection. Never <c>TKey?</c>: a
///     <c>TKey?</c> member is still matched to <c>TKey</c>.
/// </typeparam>
[AttributeUsage(AttributeTargets.Parameter)]
public class ReadModelAttribute<TNaturalKey> : ReadModelAttribute where TNaturalKey : notnull
{
    public ReadModelAttribute()
    {
        NaturalKeyTypeArgument = typeof(TNaturalKey);
    }

    public ReadModelAttribute(string argumentName) : base(argumentName)
    {
        NaturalKeyTypeArgument = typeof(TNaturalKey);
    }
}
