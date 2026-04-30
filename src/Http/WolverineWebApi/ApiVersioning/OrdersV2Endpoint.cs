using Asp.Versioning;
using Wolverine.Http;

namespace WolverineWebApi.ApiVersioning;

[ApiVersion("2.0")]
public static class OrdersV2Endpoint
{
    [WolverineGet("/orders", OperationId = "OrdersV2Endpoint.Get")]
    public static OrdersV2Response Get() => new("ok", ["v2-a", "v2-b", "v2-c"]);
}

public record OrdersV2Response(string Status, IReadOnlyList<string> Items);
