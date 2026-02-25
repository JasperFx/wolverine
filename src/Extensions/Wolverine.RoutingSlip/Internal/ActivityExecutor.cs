using Wolverine.RoutingSlip.Abstractions;
using Wolverine.RoutingSlip.Messages;

namespace Wolverine.RoutingSlip.Internal;

/// <inheritdoc />
public sealed class ActivityExecutor : IActivityExecutor
{
    /// <inheritdoc />
    public ValueTask ExecuteAsync(IMessageContext context, RoutingSlip slip, RoutingSlipExecution next)
    {
        var message = new RoutingSlipExecutionContext(Guid.NewGuid(), next, slip);
        return context.EndpointFor(next.DestinationUri).SendAsync(message);
    }

    /// <inheritdoc />
    public ValueTask CompensateAsync(IMessageContext context, RoutingSlip slip, RoutingSlipExecutionLog next)
    {
        var message = new RoutingSlipCompensationContext(next.ExecutionId, next, slip);
        return context.EndpointFor(next.DestinationUri).SendAsync(message);
    }
}
