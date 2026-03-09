namespace Wolverine.Polecat;

/// <summary>
///     Applies the aggregate handler workflow with <see cref="AggregateHandlerAttribute.AlwaysEnforceConsistency"/> set to true.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ConsistentAggregateHandlerAttribute : AggregateHandlerAttribute
{
    public ConsistentAggregateHandlerAttribute(ConcurrencyStyle loadStyle) : base(loadStyle)
    {
        AlwaysEnforceConsistency = true;
    }

    public ConsistentAggregateHandlerAttribute() : base(ConcurrencyStyle.Optimistic)
    {
        AlwaysEnforceConsistency = true;
    }
}
