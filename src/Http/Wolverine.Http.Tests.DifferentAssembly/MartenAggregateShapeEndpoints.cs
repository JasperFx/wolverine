using Wolverine.Marten;

namespace Wolverine.Http.Tests.DifferentAssembly.OpenApi;

// GH-3420 shapes: the aggregate id is resolved by the Marten aggregate handler workflow, not by the
// endpoint method signature, and the route token carries no constraint. Only Marten knows the aggregate's
// identity type, so without a seam back into the chain's ApiDescription these route parameters degrade to
// `string` in the generated OpenAPI document.
//
// These live alongside the other shape endpoints (see OpenApiShapeEndpoints.cs) — Marten's IDocumentStore
// is built to answer "what is this aggregate's id type?" but never connects to a database, so the
// openapi_shape_tests harness still renders its document with no infrastructure running.

public record ShapeOrderConfirmed;

public record ShapeOrderShipped;

public record ConfirmShapeOrder(Guid ShapeOrderId);

public class ShapeOrder
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public bool IsConfirmed { get; set; }
    public bool HasShipped { get; set; }

    public void Apply(ShapeOrderConfirmed _) => IsConfirmed = true;
    public void Apply(ShapeOrderShipped _) => HasShipped = true;
}

// The issue's shape: {id} is unconstrained and appears nowhere in the signature. The id is read off the
// command by the [AggregateHandler] workflow.
public static class MartenAggregateHandlerShapeEndpoint
{
    [AggregateHandler]
    [WolverinePost("/shapes/marten/orders/{id}/confirm")]
    public static ShapeOrderConfirmed Confirm(ConfirmShapeOrder command, ShapeOrder order) => new();
}

// The conventional {aggregate}Id route token, again unconstrained and absent from the signature.
public static class MartenAggregateHandlerConventionalRouteShapeEndpoint
{
    [AggregateHandler]
    [WolverinePost("/shapes/marten/orders/{shapeOrderId}/confirm-by-convention")]
    public static ShapeOrderConfirmed Confirm(ConfirmShapeOrder command, ShapeOrder order) => new();
}

// [WriteAggregate] binds the route value itself, so this already typed correctly before GH-3420. Pinned
// here so the aggregate shapes stay covered as one family.
public static class MartenWriteAggregateShapeEndpoint
{
    [WolverinePost("/shapes/marten/orders/{id}/ship")]
    public static ShapeOrderShipped Ship([WriteAggregate] ShapeOrder order) => new();
}
