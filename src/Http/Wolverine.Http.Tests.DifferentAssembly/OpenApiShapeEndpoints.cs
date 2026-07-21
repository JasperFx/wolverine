using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Wolverine.Http.Tests.DifferentAssembly.OpenApi;

// Endpoint shapes used by the `openapi_shape_tests` harness, which renders the *real*
// Microsoft.AspNetCore.OpenApi document for a host built on this assembly and asserts on the rendered
// operation (parameters + request body). These live here — rather than in WolverineWebApi — because
// this assembly has no database/Marten/EF dependencies, so the document can be generated from a host
// that never touches infrastructure.
//
// To add a shape: add an endpoint below, then add a [Fact] to openapi_shape_tests.cs. The Swashbuckle
// side of the same coverage lives in WolverineWebApi (see the Expect* attributes and
// open_api_generation.verify_open_api_expectations).

public record OrderLine(long OrderId, long OrderLineId);

#region GH-3380: route values bound ONLY by a compound handler

// The GH-3380 reproduction: both route ids are consumed by LoadAsync and never appear in the endpoint
// method signature. They still have to be declared as required path parameters.
public static class CompoundRouteOnlyEndpoint
{
    public static OrderLine LoadAsync([FromRoute] long orderId, [FromRoute] long orderLineId)
        => new(orderId, orderLineId);

    [WolverineGet("/shapes/orders/{orderId:long}/order-lines/{orderLineId:long}")]
    public static string Get(OrderLine line) => $"{line.OrderId}:{line.OrderLineId}";
}

// Same, but the route token is UNCONSTRAINED — so the parameter type can only come from the binding
// chain (the LoadAsync argument), not from a route constraint.
public static class CompoundUnconstrainedRouteEndpoint
{
    public static OrderLine Before([FromRoute] long orderId) => new(orderId, 0);

    [WolverineGet("/shapes/unconstrained/orders/{orderId}")]
    public static string Get(OrderLine line) => line.OrderId.ToString();
}

// A route token bound by a renamed [FromRoute(Name = "order-id")] argument on the compound handler.
public static class CompoundRenamedRouteEndpoint
{
    public static OrderLine Load([FromRoute(Name = "order-id")] long orderId) => new(orderId, 0);

    [WolverineGet("/shapes/renamed/orders/{order-id:long}")]
    public static string Get(OrderLine line) => line.OrderId.ToString();
}

// Query string + header values bound only by a compound handler method.
public record RequestContext(string? Name, string? Tenant);

public static class CompoundQueryAndHeaderEndpoint
{
    public static RequestContext Load([FromQuery] string? name, [FromHeader(Name = "x-tenant")] string? tenant)
        => new(name, tenant);

    [WolverineGet("/shapes/compound-query-header")]
    public static string Get(RequestContext context) => $"{context.Name}:{context.Tenant}";
}

// A query string value bound only by an After/Finally postprocessor. Postprocessor arguments are part of
// the endpoint's contract too.
public static class PostprocessorQueryEndpoint
{
    [WolverineGet("/shapes/postprocessor/{orderId:long}")]
    public static string Get() => "ok";

    public static void After([FromQuery] string? audit)
    {
    }
}

#endregion

#region baseline shapes: plain handler signature bindings

public record CreateOrderLine(string Description, int Quantity);

// Route + query bound the plain way, straight off the endpoint method signature.
public static class PlainRouteAndQueryEndpoint
{
    [WolverineGet("/shapes/plain/orders/{orderId:long}")]
    public static string Get([FromRoute] long orderId, [FromQuery] string? filter) => $"{orderId}:{filter}";
}

// A JSON request body alongside a route value that is bound only by the compound handler.
public static class BodyWithCompoundRouteEndpoint
{
    public static OrderLine LoadAsync([FromRoute] long orderId) => new(orderId, 0);

    [WolverinePost("/shapes/body/orders/{orderId:long}/order-lines")]
    public static string Post(CreateOrderLine body, OrderLine line) => $"{line.OrderId}:{body.Description}";
}

// Nothing in the chain binds {code} at all — it still has to be declared as a required path parameter,
// falling back to string.
public static class UnboundRouteValueEndpoint
{
    [WolverineGet("/shapes/unbound/{code}")]
    public static string Get() => "ok";
}

#endregion

#region [FromQuery] complex type shapes

// A [FromQuery] complex type is flattened into one query parameter per member. The container itself has
// no wire representation, so it must not also be declared as a parameter of its own.
public class OrderSearchQuery
{
    [FromQuery(Name = "filter")]
    public string? Filter { get; set; }

    [FromQuery(Name = "pageNumber")]
    public int PageNumber { get; set; } = 1;

    public int PageSize { get; set; } = 50;
}

public static class ComplexQueryStringEndpoint
{
    [WolverineGet("/shapes/complex-query")]
    public static string Get([FromQuery] OrderSearchQuery search)
        => $"{search.Filter}:{search.PageNumber}:{search.PageSize}";
}

// The flattened members and a second query value bound by a compound handler have to coexist.
public static class ComplexQueryStringWithCompoundHandlerEndpoint
{
    public static string Load([FromQuery] string? audit) => audit ?? "none";

    [WolverineGet("/shapes/complex-query-compound")]
    public static string Get([FromQuery] OrderSearchQuery search, string audit)
        => $"{search.Filter}:{audit}";
}

// The container is bound ONLY by the compound handler and never appears in the endpoint signature.
// Compound handler methods are parameter-matched with the endpoint method, so this flattens the same
// way — and the container must not be described here either.
public record OrderSearchResult(string Description);

public static class ComplexQueryStringOnCompoundHandlerEndpoint
{
    public static OrderSearchResult Load([FromQuery] OrderSearchQuery search)
        => new(search.Filter ?? "none");

    [WolverineGet("/shapes/complex-query-on-load")]
    public static string Get(OrderSearchResult result) => result.Description;
}

#endregion

#region [AsParameters] shapes

public record OrderLineQuery([FromRoute] long OrderId, [FromQuery] string? Filter);

public static class AsParametersEndpoint
{
    [WolverineGet("/shapes/asparameters/orders/{orderId:long}")]
    public static string Get([AsParameters] OrderLineQuery query) => $"{query.OrderId}:{query.Filter}";
}

// An [AsParameters] container on the endpoint whose route value is *also* bound by the compound handler.
public static class AsParametersWithCompoundEndpoint
{
    public static OrderLine LoadAsync([FromRoute(Name = "order-id")] long orderId) => new(orderId, 0);

    [WolverineGet("/shapes/asparameters-compound/orders/{order-id:long}")]
    public static string Get([AsParameters] RenamedOrderQuery query, OrderLine line)
        => $"{query.OrderId}:{line.OrderLineId}";
}

public class RenamedOrderQuery
{
    [FromRoute(Name = "order-id")]
    public long OrderId { get; set; }

    [FromQuery]
    public string? Filter { get; set; }
}

#endregion
