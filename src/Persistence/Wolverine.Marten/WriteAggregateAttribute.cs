using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events;
using JasperFx.Events.Aggregation;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Marten;

/// <summary>
///     Marks a parameter to a Wolverine HTTP endpoint or message handler method as being part of the Marten event sourcing
///     "aggregate handler" workflow
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class WriteAggregateAttribute : WolverineParameterAttribute, IDataRequirement, IMayInferMessageIdentity, IRefersToAggregate
{
    public WriteAggregateAttribute()
    {
    }

    public WriteAggregateAttribute(string? routeOrParameterName)
    {
        RouteOrParameterName = routeOrParameterName;
    }

    public string? RouteOrParameterName { get; }

    private OnMissing? _onMissing;

    public bool Required { get; set; } = true;
    public string MissingMessage { get; set; }

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }

    /// <summary>
    ///     Opt into exclusive locking or optimistic checks on the aggregate stream
    ///     version. Default is Optimistic
    /// </summary>
    public ConcurrencyStyle LoadStyle { get; set; } = ConcurrencyStyle.Optimistic;

    /// <summary>
    ///     If true, Marten will enforce an optimistic concurrency check on this stream even if no
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

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container, GenerationRules rules)
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

        var store = container.GetInstance<IDocumentStore>();
        var idType = store.Options.FindOrResolveDocumentType(aggregateType).IdType;

        var identity = FindIdentity(aggregateType, idType, chain);
        var isNaturalKey = false;

        // If standard identity resolution failed, check for natural key support
        if (identity == null && store.Options is StoreOptions storeOptions)
        {
            var naturalKey = storeOptions.Projections.FindNaturalKeyDefinition(aggregateType);
            if (naturalKey != null)
            {
                identity = FindIdentity(aggregateType, naturalKey.OuterType, chain);
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
                    if (chain.TryFindVariable(matchingProps[0].Name, ValueSource.Anything, strongTypedIdType, out var v4))
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

    internal static Type? FindIdentifiedByType(Type aggregateType)
    {
        var identifiedByInterface = aggregateType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IdentifiedBy<>));

        return identifiedByInterface?.GetGenericArguments()[0];
    }

    public bool TryInferMessageIdentity(IChain chain, out PropertyInfo property)
    {
        var inputType = chain.InputType();
        if (inputType == null)
        {
            property = default;
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