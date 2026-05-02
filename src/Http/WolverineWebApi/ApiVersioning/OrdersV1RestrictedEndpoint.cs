using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace WolverineWebApi.ApiVersioning;

[ApiVersion("1.0")]
public static class OrdersV1RestrictedEndpoint
{
    public static IResult Before(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey("X-Test-Auth"))
        {
            return Results.Unauthorized();
        }

        return WolverineContinue.Result();
    }

    [WolverineGet("/orders/restricted", OperationId = "OrdersV1RestrictedEndpoint.Get")]
    public static string Get() => "restricted-ok";
}
