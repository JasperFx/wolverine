namespace Wolverine.Polecat;

/// <summary>
///     Marks a parameter to a Wolverine message handler as being part of the Polecat event sourcing
///     "aggregate handler" workflow with <see cref="WriteAggregateAttribute.AlwaysEnforceConsistency"/> set to true.
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
