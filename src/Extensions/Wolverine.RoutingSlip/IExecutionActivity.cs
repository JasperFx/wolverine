namespace Wolverine.RoutingSlip;

public interface IExecutionActivity
{
    ValueTask HandleAsync(ExecutionContext context, CancellationToken ct);
}