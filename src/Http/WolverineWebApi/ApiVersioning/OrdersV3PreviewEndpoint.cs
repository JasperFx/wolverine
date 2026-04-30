using Asp.Versioning;
using Wolverine.Http;

namespace WolverineWebApi.ApiVersioning;

[ApiVersion("3.0")]
public static class OrdersV3PreviewEndpoint
{
    [WolverineGet("/orders", OperationId = "OrdersV3PreviewEndpoint.Get")]
    public static OrdersV2Response Get() => new("preview", ["v3-preview"]);
}
