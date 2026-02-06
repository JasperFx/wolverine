using Wolverine.RoutingSlip.Abstractions;

namespace Wolverine.RoutingSlip.Internal;

/// <inheritdoc />
public class ActivityExecutor() : IActivityExecutor
{
    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IMessageContext context, RoutingSlip slip, RoutingSlipExecution next)
    {
        var msg = new ExecutionContext(Guid.NewGuid(), next, slip);
        await context.EndpointFor(next.DestinationUri).SendAsync(msg);
    }
    
    /// <inheritdoc />
    public async ValueTask CompensateAsync(IMessageContext context, RoutingSlip slip, RoutingSlipExecutionLog next)
    {
        var msg = new CompensationContext(next.ExecutionId, next, slip);
        await context.EndpointFor(next.DestinationUri).SendAsync(msg);
    }
}