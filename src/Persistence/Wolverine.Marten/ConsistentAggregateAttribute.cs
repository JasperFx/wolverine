namespace Wolverine.Marten;

/// <summary>
///     Marks a parameter to a Wolverine message handler as being part of the Marten event sourcing
///     "aggregate handler" workflow with <see cref="WriteAggregateAttribute.AlwaysEnforceConsistency"/> set to true,
///     meaning Marten will enforce an optimistic concurrency check on referenced streams even if no events are appended.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class ConsistentAggregateAttribute : WriteAggregateAttribute
{
    public ConsistentAggregateAttribute() : base()
    {
        AlwaysEnforceConsistency = true;
    }

    public ConsistentAggregateAttribute(string? routeOrParameterName) : base(routeOrParameterName)
    {
        AlwaysEnforceConsistency = true;
    }
}
