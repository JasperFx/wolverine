using Asp.Versioning;
using Wolverine.Http;

namespace WolverineWebApi.ApiVersioning;

[ApiVersion("1.0")]
public static class OrdersV1ThrowsEndpoint
{
    [WolverineGet("/orders/throws", OperationId = "OrdersV1ThrowsEndpoint.Get")]
    public static string Get()
        => throw new InvalidOperationException("intentional failure for exception-handler regression test");
}
