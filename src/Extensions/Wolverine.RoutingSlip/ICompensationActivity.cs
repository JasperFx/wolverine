namespace Wolverine.RoutingSlip;

public interface ICompensationActivity
{
    ValueTask HandleAsync(CompensationContext context, CancellationToken ct);
}