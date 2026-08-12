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

    public bool Required { get; set; } = true;
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

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Handler/command/model types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        _onMissing ??= container.GetInstance<WolverineOptions>().EntityDefaults.OnMissing;
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
        var provider = rules.FindEventSourcingFrameProvider(container, aggregateType);

        // Both stores had this inlined, spelled differently, and both spellings were byte-for-byte their
        // own IPersistenceFrameProvider.DetermineSagaIdType. Marten resolved the document type's IdType;
        // Polecat reflected over the Id property. It's the same seam, which already existed.
        var idType = ((IPersistenceFrameProvider)provider).DetermineSagaIdType(aggregateType, container);

        // If a specific ValueSource has been set (e.g. via FromMethod, FromRoute, FromHeader, FromClaim),
        // use the base class identity resolution which respects that ValueSource
        Variable? identity = null;
        if (ValueSource != ValueSource.InputMember && ArgumentName.IsNotEmpty())
        {
            tryFindIdentityVariable(chain, parameter, idType, out identity);
        }

        // Fall back to WriteModel's standard identity resolution
        identity ??= FindIdentity(aggregateType, idType, chain);
        var isNaturalKey = false;

        // If standard identity resolution failed, check for natural key support
        if (identity == null)
        {
            var naturalKeyType = provider.TryDetermineNaturalKeyType(aggregateType, container);
            if (naturalKeyType != null)
            {
                identity = FindIdentity(aggregateType, naturalKeyType, chain);
                if (identity != null) isNaturalKey = true;
            }
        }

        if (identity == null)
        {
            throw new InvalidOperationException(
                $"Unable to determine an aggregate id for the parameter '{parameter.Name}' on method {chain.HandlerCalls().First()}");
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
            IsNaturalKey = isNaturalKey
        };

        return handling.Apply(chain, container);
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
