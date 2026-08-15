using Wolverine.Fisher;

namespace Wolverine.Http.Fisher;

/// <summary>
///     Marks a parameter to an HTTP endpoint as being part of the Fisher event sourcing
///     "aggregate handler" workflow with <see cref="WriteAggregateAttribute.AlwaysEnforceConsistency"/> set to true,
///     meaning Fisher will enforce an optimistic concurrency check on referenced streams even if no events are appended.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class ConsistentAggregateAttribute : Wolverine.Fisher.ConsistentAggregateAttribute
{
    public ConsistentAggregateAttribute()
    {
    }

    public ConsistentAggregateAttribute(string? routeOrParameterName) : base(routeOrParameterName)
    {
    }
}
