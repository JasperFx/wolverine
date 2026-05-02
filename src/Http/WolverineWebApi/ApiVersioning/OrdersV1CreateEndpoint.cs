using Asp.Versioning;
using FluentValidation;
using Wolverine.Http;

namespace WolverineWebApi.ApiVersioning;

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
