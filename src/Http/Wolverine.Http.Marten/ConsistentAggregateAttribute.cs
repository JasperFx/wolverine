using Wolverine.Marten;

namespace Wolverine.Http.Marten;

/// <summary>
///     Marks a parameter to an HTTP endpoint as being part of the Marten event sourcing
///     "aggregate handler" workflow with <see cref="WriteAggregateAttribute.AlwaysEnforceConsistency"/> set to true,
///     meaning Marten will enforce an optimistic concurrency check on referenced streams even if no events are appended.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class ConsistentAggregateAttribute : Wolverine.Marten.ConsistentAggregateAttribute
{
    public ConsistentAggregateAttribute()
    {
    }

    public ConsistentAggregateAttribute(string? routeOrParameterName) : base(routeOrParameterName)
    {
    }
}
