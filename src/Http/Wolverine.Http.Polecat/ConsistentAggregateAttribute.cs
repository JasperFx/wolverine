using Wolverine.Polecat;

namespace Wolverine.Http.Polecat;

/// <summary>
///     Marks a parameter to an HTTP endpoint as being part of the Polecat event sourcing
///     "aggregate handler" workflow with <see cref="WriteAggregateAttribute.AlwaysEnforceConsistency"/> set to true,
///     meaning Polecat will enforce an optimistic concurrency check on referenced streams even if no events are appended.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class ConsistentAggregateAttribute : Wolverine.Polecat.ConsistentAggregateAttribute
{
    public ConsistentAggregateAttribute()
    {
    }

    public ConsistentAggregateAttribute(string? routeOrParameterName) : base(routeOrParameterName)
    {
    }
}
