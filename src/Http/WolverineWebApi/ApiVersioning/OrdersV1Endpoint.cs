using Asp.Versioning;
using Wolverine.Http;

namespace WolverineWebApi.ApiVersioning;

[ApiVersion("1.0")]
public static class OrdersV1Endpoint
{
    [WolverineGet("/orders", OperationId = "OrdersV1Endpoint.Get")]
    public static OrdersV1Response Get() => new(["v1-order-1", "v1-order-2"]);
}

public record OrdersV1Response(IReadOnlyList<string> Orders);
