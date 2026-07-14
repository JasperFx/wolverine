using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using Wolverine.Http;
using WolverineWebApi.TestSupport;

namespace WolverineWebApi;

// GH-3380 OpenAPI-shape coverage for compound handlers on the *Swashbuckle* stack: route/query/header
// values bound ONLY by a Load/LoadAsync/Before middleware method or an After/Finally postprocessor —
// values that never appear in the endpoint method signature — must still be declared on the generated
// operation. The expectations below are validated by open_api_generation.verify_open_api_expectations.
//
// The Microsoft.AspNetCore.OpenApi half of the same coverage (plus more shapes) lives in
// Wolverine.Http.Tests/openapi_shape_tests.cs.
//
// These endpoints are deliberately free of database dependencies so they participate in the
// Swashbuckle-driven open_api_generation harness.

public record Bug3380OrderLine(long OrderId, long OrderLineId);

// The GH-3380 reproduction: BOTH route ids are consumed only by LoadAsync
public static class CompoundHandlerRouteOnlyEndpoint
{
    public static Bug3380OrderLine LoadAsync([FromRoute] long orderId, [FromRoute] long orderLineId)
    {
        return new Bug3380OrderLine(orderId, orderLineId);
    }

    [WolverineGet("/api/3380/orders/{orderId:long}/order-lines/{orderLineId:long}")]
    [ExpectParameter("orderId", ParameterLocation.Path, Type = "integer", Format = "int64", Required = true)]
    [ExpectParameter("orderLineId", ParameterLocation.Path, Type = "integer", Format = "int64", Required = true)]
    [ExpectParameterCount(2)]
    [ExpectNoRequestBody]
    public static string Get(Bug3380OrderLine line) => $"{line.OrderId}:{line.OrderLineId}";
}

// Query + header bound only by a compound handler, and a query value bound only by an After postprocessor
public record Bug3380Context(string? Name, string? Tenant);

public static class CompoundHandlerQueryHeaderEndpoint
{
    public static Bug3380Context Load([FromQuery] string? name, [FromHeader(Name = "x-tenant")] string? tenant)
    {
        return new Bug3380Context(name, tenant);
    }

    [WolverineGet("/api/3380/context")]
    [ExpectParameter("name", ParameterLocation.Query, Type = "string", Required = false)]
    [ExpectParameter("x-tenant", ParameterLocation.Header, Type = "string", Required = false)]
    [ExpectParameter("audit", ParameterLocation.Query, Type = "string", Required = false)]
    [ExpectParameterCount(3)]
    [ExpectNoRequestBody]
    public static string Get(Bug3380Context context) => $"{context.Name}:{context.Tenant}";

    public static void After([FromQuery] string? audit)
    {
    }
}

// A JSON request body alongside a route value bound only by the compound handler
public record Bug3380CreateLine(string Description, int Quantity);

public static class CompoundHandlerWithBodyEndpoint
{
    public static Bug3380OrderLine LoadAsync([FromRoute] long orderId) => new(orderId, 0);

    [WolverinePost("/api/3380/orders/{orderId:long}/order-lines")]
    [ExpectParameter("orderId", ParameterLocation.Path, Type = "integer", Format = "int64", Required = true)]
    [ExpectParameterCount(1)]
    [ExpectRequestBody("application/json", "description", "quantity", ForbiddenProperties = ["orderId"])]
    public static string Post(Bug3380CreateLine body, Bug3380OrderLine line) => $"{line.OrderId}:{body.Description}";
}
