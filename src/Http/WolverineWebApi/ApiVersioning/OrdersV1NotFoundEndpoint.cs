using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace WolverineWebApi.ApiVersioning;

[ApiVersion("1.0")]
public static class OrdersV1NotFoundEndpoint
{
    [WolverineGet("/orders/{id}", OperationId = "OrdersV1NotFoundEndpoint.GetById")]
    public static IResult GetById(string id) => Results.NotFound(new { id });
}
