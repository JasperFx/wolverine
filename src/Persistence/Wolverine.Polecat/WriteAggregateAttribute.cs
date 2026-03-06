using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Polecat.Events;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Polecat;

/// <summary>
///     Marks a parameter to a Wolverine HTTP endpoint or message handler method as being part of the Polecat event sourcing
///     "aggregate handler" workflow
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class WriteAggregateAttribute : WolverineParameterAttribute, IDataRequirement, IMayInferMessageIdentity, IRefersToAggregate
{
    public WriteAggregateAttribute() { }
    public WriteAggregateAttribute(string? routeOrParameterName) { RouteOrParameterName = routeOrParameterName; }

    public string? RouteOrParameterName { get; }

    private OnMissing? _onMissing;
    public bool Required { get; set; } = true;
    public string MissingMessage { get; set; }

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }

    public ConcurrencyStyle LoadStyle { get; set; } = ConcurrencyStyle.Optimistic;
    public bool AlwaysEnforceConsistency { get; set; }
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

        var idProp = aggregateType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var idType = idProp?.PropertyType ?? typeof(Guid);

        var identity = FindIdentity(aggregateType, idType, chain);
        var isNaturalKey = false;

        // If standard identity resolution failed, check for natural key support
        if (identity == null)
        {
            var storeOptions = container.Services.GetRequiredService<StoreOptions>();
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
        if (VersionSource == null && chain.Tags.ContainsKey(nameof(AggregateHandling)))
        {
            return null;
        }

        var name = VersionSource ?? "version";

        if (chain.TryFindVariable(name, ValueSource.Anything, typeof(long), out var variable)) return variable;
        if (chain.TryFindVariable(name, ValueSource.Anything, typeof(int), out var v2)) return v2;
        if (chain.TryFindVariable(name, ValueSource.Anything, typeof(uint), out var v3)) return v3;

        return null;
    }

    public Variable? FindIdentity(Type aggregateType, Type idType, IChain chain)
    {
        if (RouteOrParameterName.IsNotEmpty())
        {
            if (chain.TryFindVariable(RouteOrParameterName, ValueSource.Anything, idType, out var variable))
                return variable;
        }

        if (chain.TryFindVariable($"{aggregateType.Name.ToCamelCase()}Id", ValueSource.Anything, idType, out var v2))
            return v2;

        if (chain.TryFindVariable("id", ValueSource.Anything, idType, out var v3))
            return v3;

        var strongTypedIdType = idType;
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
                        return v4;
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
