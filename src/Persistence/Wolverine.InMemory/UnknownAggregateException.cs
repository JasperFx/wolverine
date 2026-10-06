using JasperFx.Core.Reflection;

namespace Wolverine.InMemory;

/// <summary>
/// Thrown when a required aggregate is not found in the in-memory prototyping store.
/// </summary>
public class UnknownAggregateException : Exception
{
    public UnknownAggregateException(Type aggregateType, object id) : base(
        $"Could not find an aggregate of type {aggregateType.FullNameInCode()} with id {id}. The parameter is required; to handle a missing stream in the handler instead of failing, make the parameter nullable or use [WriteAggregate(Required = false)] (or the equivalent on [ReadAggregate]/[Aggregate]).")
    {
    }
}
