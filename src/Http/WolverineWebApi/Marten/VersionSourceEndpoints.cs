using Wolverine.Http;
using Wolverine.Http.Marten;
using Wolverine.Marten;
using WolverineWebApi.Marten;

namespace WolverineWebApi.Marten;

public record ShipOrderWithExpectedVersion(Guid OrderId, long ExpectedVersion);

public static class VersionSourceEndpoints
{
    // Route argument version with custom name
    [WolverinePost("/orders/{orderId}/ship-with-expected-version/{expectedVersion}")]
    [EmptyResponse]
    public static OrderShipped ShipWithRouteVersion(
        ShipOrderWithExpectedVersion command,
        [Aggregate(VersionSource = "expectedVersion")] Order order)
    {
        return new OrderShipped();
    }

    // Request body member version with custom name
    [WolverinePost("/orders/ship-with-body-version")]
    [EmptyResponse]
    public static OrderShipped ShipWithBodyVersion(
        ShipOrderWithExpectedVersion command,
        [Aggregate(VersionSource = nameof(ShipOrderWithExpectedVersion.ExpectedVersion))] Order order)
    {
        return new OrderShipped();
    }
}
