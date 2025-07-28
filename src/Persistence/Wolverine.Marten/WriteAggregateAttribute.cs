using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
///     Marks a parameter to a Wolverine HTTP endpoint or message handler method as being part of the Marten event sourcing
///     "aggregate handler" workflow
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class WriteAggregateAttribute : WolverineParameterAttribute, IDataRequirement
{
    public WriteAggregateAttribute()
    {
    }

    public WriteAggregateAttribute(string? routeOrParameterName)
    {
        RouteOrParameterName = routeOrParameterName;
    }

    public string? RouteOrParameterName { get; }

    public bool Required { get; set; }
    public string MissingMessage { get; set; }
    public OnMissing OnMissing { get; set; }

    /// <summary>
    ///     Opt into exclusive locking or optimistic checks on the aggregate stream
    ///     version. Default is Optimistic
    /// </summary>
    public ConcurrencyStyle LoadStyle { get; set; } = ConcurrencyStyle.Optimistic;

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container, GenerationRules rules)
    {
        // TODO -- this goes away soon-ish
        if (chain.HandlerCalls().First().Method.GetParameters().Count(x => x.HasAttribute<WriteAggregateAttribute>()) > 1)
        {
            throw new InvalidOperationException(
                "It is only possible (today) to use a single [Aggregate] attribute on an HTTP handler method. Maybe use [ReadAggregate] if all you need is the projected data");
        }

        var aggregateType = parameter.ParameterType;
        if (aggregateType.IsNullable())
        {
            aggregateType = aggregateType.GetInnerTypeFromNullable();
        }

        var store = container.GetInstance<IDocumentStore>();
        var idType = store.Options.FindOrResolveDocumentType(aggregateType).IdType;

        var identity = FindIdentity(aggregateType, idType, chain) ?? throw new InvalidOperationException(
            $"Unable to determine an aggregate id for the parameter '{parameter.Name}' on method {chain.HandlerCalls().First()}");

        if (identity == null)
        {
            throw new InvalidOperationException(
                "Cannot determine an identity variable for this aggregate from the route arguments");
        }

        var version = findVersionVariable(chain);

        // Store information about the aggregate handling in the chain just in
        // case they're using LatestAggregate
        var handling = new AggregateHandling(this)
        {
            AggregateType = aggregateType,
            AggregateId = identity,
            LoadStyle = LoadStyle,
            Version = version
        };

        return handling.Apply(chain, container);
    }
    
    internal Variable? findVersionVariable(IChain chain)
    {
        if (chain.TryFindVariable("version", ValueSource.Anything, typeof(long), out var variable))
        {
            return variable;
        }

        if (chain.TryFindVariable("version", ValueSource.Anything, typeof(int), out var v2))
        {
            return v2;
        }

        if (chain.TryFindVariable("version", ValueSource.Anything, typeof(uint), out var v3))
        {
            return v3;
        }

        if (chain.TryFindVariable("version", ValueSource.Anything, typeof(uint), out var v4))
        {
            return v4;
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

        return null;
    }
}