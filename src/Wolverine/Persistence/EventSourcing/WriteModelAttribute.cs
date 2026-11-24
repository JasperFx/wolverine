using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Marks a parameter to a Wolverine HTTP endpoint or message handler method as an event sourced model
///     that the handler is going to <b>write</b> to: the aggregate is loaded with concurrency protection,
///     and whatever events the handler returns are appended to its stream.
/// </summary>
/// <remarks>
///     <para>
///     Store-agnostic — it works against whichever event store integration claims the aggregate type, so
///     the same handler signature is valid on Marten, on Polecat, or on any integration that implements
///     <see cref="IEventSourcingFrameProvider" />.
///     </para>
///     <para>
///     <c>Wolverine.Marten.WriteAggregateAttribute</c> and <c>Wolverine.Polecat.WriteAggregateAttribute</c>
///     both derive from this and are kept as-is for existing code. GH-3907.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class WriteModelAttribute : WolverineParameterAttribute, IDataRequirement, IMayInferMessageIdentity,
    IRefersToAggregate
{
    public WriteModelAttribute()
    {
    }

    public WriteModelAttribute(string? routeOrParameterName)
    {
        RouteOrParameterName = routeOrParameterName;
    }

    public string? RouteOrParameterName { get; }

    private OnMissing? _onMissing;
    private bool? _required;

    /// <summary>
    ///     Should Wolverine stop the handler when the model cannot be found? Defaults to <b>the opposite of
    ///     the parameter's nullable annotation</b>: <c>Order order</c> is required, <c>Order? order</c> is not.
    /// </summary>
    /// <remarks>
    ///     GH-3916. Before this the default was an unconditional <c>true</c>, so a parameter annotated
    ///     nullable — the author saying "I will handle absence" — still generated an
    ///     <c>EntityIsNotNullGuard</c> and a <c>HandlerContinuation.Stop</c>, making the handler's own null
    ///     branch dead code and logging a warning per message. A nullable annotation with
    ///     <c>Required = true</c> is a contradiction; the annotation is the more specific signal, and an
    ///     explicit <c>Required</c> at the call site still wins over both.
    /// </remarks>
    public bool Required
    {
        get => _required ?? true;
        set => _required = value;
    }

    public string MissingMessage { get; set; } = null!;

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }

    /// <summary>
    ///     Opt into exclusive locking or optimistic checks on the aggregate stream
    ///     version. Default is Optimistic
    /// </summary>
    public ModelConcurrencyStyle LoadStyle { get; set; } = ModelConcurrencyStyle.Optimistic;

    /// <summary>
    ///     If true, the event store will enforce an optimistic concurrency check on this stream even if no
    ///     events are appended at the time of calling SaveChangesAsync(). This is useful when you want
    ///     to ensure the stream version has not advanced since it was fetched, even if the command
    ///     handler decides not to emit any new events.
    /// </summary>
    public bool AlwaysEnforceConsistency { get; set; }

    /// <summary>
    ///     Override the name of the variable or member used to find the expected stream version
    ///     for optimistic concurrency checks. By default, Wolverine looks for a variable named "version".
    ///     This is useful in multi-stream operations where each stream needs its own version source.
    /// </summary>
    public string? VersionSource { get; set; }

    /// <summary>
    ///     Load the model by this natural key type rather than by its stream id: the model's own natural key,
    ///     or one it maps from another projection. The key is found as a member, route value or other value of
    ///     that type (or <c>TKey?</c> for a struct key) by the name given to the attribute, <c>[Identity]</c>,
    ///     the naming conventions, or the only member of that type. <c>[WriteModel&lt;TKey&gt;]</c> and
    ///     <c>[WriteAggregate&lt;TKey&gt;]</c> set this.
    /// </summary>
    /// <remarks>
    ///     Without it, a natural key is only tried after the stream id lookup finds nothing, which never happens
    ///     for an HTTP route or query string value: those parse into a stream id of any type.
    /// </remarks>
    public Type? NaturalKey
    {
        get => _naturalKey ?? NaturalKeyTypeArgument;
        set => _naturalKey = value;
    }

    private Type? _naturalKey;

    /// <summary>
    ///     The natural key type a generic spelling such as <c>[WriteModel&lt;TKey&gt;]</c> names as its type argument.
    ///     Kept apart from <see cref="NaturalKey" /> so that naming two different key types, as in
    ///     <c>[WriteModel&lt;OrderNumber&gt;(NaturalKey = typeof(CustomerCode))]</c>, is refused rather than settled
    ///     silently in favour of <see cref="NaturalKey" />. GH-5947.
    /// </summary>
    protected Type? NaturalKeyTypeArgument { get; set; }

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Handler/command/model types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        _onMissing ??= container.GetInstance<WolverineOptions>().EntityDefaults.OnMissing;

        // GH-3916: only when the call site said nothing. An explicit [WriteModel(Required = true)] on a
        // nullable parameter still gets its guard - loudly wrong beats silently overridden.
        _required ??= DefaultRequired(parameter);

        var aggregateType = parameter.ParameterType;
        if (aggregateType.IsNullable())
        {
            aggregateType = aggregateType.GetInnerTypeFromNullable();
        }

        if (aggregateType.Closes(typeof(IEventStream<>)))
        {
            aggregateType = aggregateType.GetGenericArguments()[0];
        }

        // GH-3907: which store owns this aggregate is resolved out of the persistence strategies already
        // registered on these GenerationRules - the same registry [Entity] resolves through - so nothing
        // here names a store.
        var provider = ResolveEventSourcingProvider(rules, container, aggregateType);

        // Both stores had this inlined, spelled differently, and both spellings were byte-for-byte their
        // own IPersistenceFrameProvider.DetermineSagaIdType. Marten resolved the document type's IdType;
        // Polecat reflected over the Id property. It's the same seam, which already existed.
        //
        // GH-4441: the chain goes over that seam too, for the same reason the natural-key lookup below does.
        // Marten's answer is a fact about the store, so a chain routed to an ancillary store has to ask that
        // store rather than the default one.
        var idType = ((IPersistenceFrameProvider)provider).DetermineSagaIdType(aggregateType, chain, container);

        Variable? identity = null;
        var isNaturalKey = false;
        var spelling = NaturalKeyIdentity.SpellingOf(this);

        NaturalKeyIdentity.AssertOneKeyType(NaturalKeyTypeArgument, _naturalKey, spelling, aggregateType, chain);

        if (NaturalKey != null)
        {
            // Named explicitly, so the stream id is never considered
            var (name, source) = ArgumentName.IsNotEmpty()
                ? (ArgumentName, ValueSource)
                : (RouteOrParameterName, ValueSource.Anything);

            identity = NaturalKeyIdentity.FindByKeyType(provider, aggregateType, NaturalKey, chain, container,
                name, source);
            isNaturalKey = true;
        }
        else
        {
            // If a specific ValueSource has been set (e.g. via FromMethod, FromRoute, FromHeader, FromClaim),
            // use the base class identity resolution which respects that ValueSource
            if (ValueSource != ValueSource.InputMember && ArgumentName.IsNotEmpty())
            {
                tryFindIdentityVariable(chain, parameter, idType, out identity);
            }

            // A stream id named in the attribute comes ahead of [Identity] in FindIdentity too
            if (identity == null && RouteOrParameterName.IsNotEmpty() &&
                chain.TryFindVariable(RouteOrParameterName, ValueSource.Anything, idType, out var named))
            {
                identity = named;
                NaturalKeyIdentity.WarnOfUntypedRouteValue(provider, aggregateType, RouteOrParameterName, chain,
                    container, spelling);
            }

            // Fall back to WriteModel's standard identity resolution. Only now could an [Identity] member be taken
            // as the stream id, so only now is one of a natural key type refused: with the stream id named above,
            // that member is never read, and the handler is fine as it is.
            if (identity == null)
            {
                NaturalKeyIdentity.AssertIdentityIsNotANaturalKey(provider, aggregateType, idType, chain, container,
                    spelling);
                identity = FindIdentity(aggregateType, idType, chain);
            }
        }

        // If standard identity resolution failed, check for natural key support
        if (identity == null)
        {
            // A copy for the lambda: capturing aggregateType itself would hoist it into a closure, which the
            // trim analysis cannot follow through to its earlier uses
            var keyedAggregateType = aggregateType;
            identity = NaturalKeyIdentity.Find(provider, aggregateType, chain, container,
                keyType => FindIdentity(keyedAggregateType, keyType, chain), spelling);
            isNaturalKey = identity != null;
        }

        if (identity == null)
        {
            throw new InvalidOperationException(
                $"Unable to determine an aggregate id for the parameter '{parameter.Name}' on method {chain.HandlerCalls().First()}." +
                NaturalKeyIdentity.ExplainMissingIdentity(provider, aggregateType, idType, chain, container, spelling));
        }

        var version = findVersionVariable(chain);

        // Store information about the aggregate handling in the chain just in
        // case they're using LatestAggregate
        var handling = new AggregateHandling(this)
        {
            AggregateType = aggregateType,
            AggregateId = identity,
            Provider = provider,
            LoadStyle = LoadStyle,
            Version = version,
            AlwaysEnforceConsistency = AlwaysEnforceConsistency,
            Parameter = parameter,
            IsNaturalKey = isNaturalKey,
            StreamIdType = idType
        };

        return handling.Apply(chain, container);
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

    internal Variable? findVersionVariable(IChain chain)
    {
        // If no explicit VersionSource is set and another aggregate handling already
        // exists on this chain, skip automatic version discovery to avoid multiple
        // streams accidentally sharing the same "version" variable
        if (VersionSource == null && chain.Tags.ContainsKey(nameof(AggregateHandling)))
        {
            return null;
        }

        var name = VersionSource ?? "version";

        if (chain.TryFindVariable(name, ValueSource.Anything, typeof(long), out var variable))
        {
            return variable;
        }

        if (chain.TryFindVariable(name, ValueSource.Anything, typeof(int), out var v2))
        {
            return v2;
        }

        if (chain.TryFindVariable(name, ValueSource.Anything, typeof(uint), out var v3))
        {
            return v3;
        }

        return null;
    }

    /// <summary>
    ///     The value <see cref="Required" /> takes when the call site did not set it. Defaults to the
    ///     opposite of the parameter's nullable annotation (GH-3916).
    /// </summary>
    /// <remarks>
    ///     GH-3929: virtual so that a store's own spelling of this workflow can keep the default it
    ///     shipped with. <c>[WriteAggregate]</c> predates the nullability inference and overrides this to
    ///     an unconditional <c>true</c>, because changing the default under existing code is a silent
    ///     behaviour change that only shows up at runtime.
    /// </remarks>
    protected virtual bool DefaultRequired(ParameterInfo parameter)
    {
        return !ParameterNullability.IsNullableAnnotated(parameter);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Handler/command/model types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    public Variable? FindIdentity(Type aggregateType, Type idType, IChain chain)
    {
        if (RouteOrParameterName.IsNotEmpty())
        {
            if (chain.TryFindVariable(RouteOrParameterName, ValueSource.Anything, idType, out var variable))
            {
                return variable;
            }
        }

        // GH-3918: [Identity] is the declared, discoverable way to say "this member is the identity", and
        // it lives on the message where it is true no matter which handler form consumes it.
        // [DeciderFunction] has always honored it (AggregateHandling.DetermineAggregateIdMember); this is
        // the same match, by attribute *name* so a store's own [Identity] spelling works without core
        // enumerating stores. Ahead of the name conventions, behind an explicit [WriteModel("...")].
        if (tryFindIdentityMarkedVariable(chain, out var marked))
        {
            return marked;
        }

        if (chain.TryFindVariable($"{aggregateType.Name.ToCamelCase()}Id", ValueSource.Anything, idType, out var v2))
        {
            return v2;
        }

        if (chain.TryFindVariable("id", ValueSource.Anything, idType, out var v3))
        {
            return v3;
        }

        // Fall back to strong typed identifier matching: if the identity type is a
        // strong typed ID (not a primitive like Guid/string), look for a single property
        // of that exact type on the input/command type.
        var strongTypedIdType = idType;

        // If idType is primitive, check if the aggregate declares IdentifiedBy<T>
        if (IsPrimitiveIdType(idType))
        {
            strongTypedIdType = FindIdentifiedByType(aggregateType);
        }

        if (strongTypedIdType != null && !IsPrimitiveIdType(strongTypedIdType))
        {
            var inputType = chain.InputType();
            if (inputType != null)
            {
                var matchingProps = inputType.GetProperties()
                    .Where(x => x.PropertyType == strongTypedIdType && x.CanRead)
                    .ToArray();

                if (matchingProps.Length == 1)
                {
                    if (chain.TryFindVariable(matchingProps[0].Name, ValueSource.Anything, strongTypedIdType,
                            out var v4))
                    {
                        return v4;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Look for a member on the message/request type marked with <c>[Identity]</c>, and resolve the chain
    ///     variable that carries it. GH-3918.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Handler/command/model types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    private static bool tryFindIdentityMarkedVariable(IChain chain, [NotNullWhen(true)] out Variable? variable)
    {
        variable = null;

        var inputType = chain.InputType();
        if (inputType == null) return false;

        foreach (var member in inputType.GetMembers().Where(AggregateHandling.IsMarkedAsIdentity))
        {
            // Match on the member's own type rather than the model's id type: a strong typed id member
            // and a raw Guid member are both legitimate here, and the workflow unwraps the former later.
            var memberType = (member as PropertyInfo)?.PropertyType ?? (member as FieldInfo)?.FieldType;
            if (memberType == null) continue;

            if (chain.TryFindVariable(member.Name, ValueSource.InputMember, memberType, out variable))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsPrimitiveIdType(Type type)
    {
        return type == typeof(Guid) || type == typeof(string) || type == typeof(int) || type == typeof(long);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Handler/command/model types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    internal static Type? FindIdentifiedByType(Type aggregateType)
    {
        var identifiedByInterface = aggregateType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IdentifiedBy<>));

        return identifiedByInterface?.GetGenericArguments()[0];
    }

    public bool TryInferMessageIdentity(IChain chain, [NotNullWhen(true)] out PropertyInfo? property)
    {
        var inputType = chain.InputType();
        if (inputType == null)
        {
            property = null;
            return false;
        }

        // NOT PROUD OF THIS CODE!
        if (AggregateHandling.TryLoad(chain, out var handling))
        {
            if (handling.AggregateId is MemberAccessVariable mav)
            {
                property = mav.Member as PropertyInfo;
                return property != null;
            }
        }

        property = null;
        return false;
    }
}

/// <summary>
///     <see cref="WriteModelAttribute" /> loading the model by its <typeparamref name="TNaturalKey" /> natural
///     key. Equivalent to <c>[WriteModel(NaturalKey = typeof(TNaturalKey))]</c>.
/// </summary>
/// <typeparam name="TNaturalKey">
///     The model's own natural key type, or one it maps from another projection. Never <c>TKey?</c>: a
///     <c>TKey?</c> member is still matched to <c>TKey</c>.
/// </typeparam>
[AttributeUsage(AttributeTargets.Parameter)]
public class WriteModelAttribute<TNaturalKey> : WriteModelAttribute where TNaturalKey : notnull
{
    public WriteModelAttribute()
    {
        NaturalKeyTypeArgument = typeof(TNaturalKey);
    }

    public WriteModelAttribute(string? routeOrParameterName) : base(routeOrParameterName)
    {
        NaturalKeyTypeArgument = typeof(TNaturalKey);
    }
}
