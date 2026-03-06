namespace Wolverine.Marten;

/// <summary>
///     Applies the aggregate handler workflow with <see cref="AggregateHandlerAttribute.AlwaysEnforceConsistency"/> set to true,
///     meaning Marten will enforce an optimistic concurrency check on referenced streams even if no events are appended.
///     This is useful for cross-stream operations where you want to ensure referenced aggregates have not changed
///     since they were fetched.
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
