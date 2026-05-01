using Asp.Versioning;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace WolverineWebApi.ApiVersioning;

[ApiVersion("1.0")]
public static class OrdersV1NotFoundEndpoint
{
    [WolverineGet("/orders/{id}", OperationId = "OrdersV1NotFoundEndpoint.GetById")]
    public static IResult GetById(string id) => Results.NotFound(new { id });
}

public record CreateOrderV1Request(string Sku, int Quantity)
{
    public class Validator : AbstractValidator<CreateOrderV1Request>
    {
        public Validator()
        {
            RuleFor(x => x.Sku).NotEmpty();
            RuleFor(x => x.Quantity).GreaterThan(0);
        }
    }
}

[ApiVersion("1.0")]
public static class OrdersV1CreateEndpoint
{
    [WolverinePost("/orders", OperationId = "OrdersV1CreateEndpoint.Create")]
    public static string Create(CreateOrderV1Request request) => "created";
}

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
