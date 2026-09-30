using Wolverine.Persistence;

namespace Wolverine.Http.Tests.DifferentAssembly.OpenApi;

// The response an [Entity] miss advertises in the generated OpenAPI document. A problem-details miss
// must carry the ProblemDetails body it actually writes; a Simple404 writes no body and must not claim
// one. Loaded through Marten, which (like the aggregate shapes) never connects while the document renders.

public class ShapeWidget
{
    public string Id { get; set; } = null!;
    public string? Name { get; set; }
}

public static class EntityMissShapeEndpoints
{
    [WolverineGet("/shapes/entity-miss/simple404/{id}")]
    public static ShapeWidget GetSimple404([Entity(OnMissing = OnMissing.Simple404)] ShapeWidget widget) => widget;

    [WolverineGet("/shapes/entity-miss/problem400/{id}")]
    public static ShapeWidget GetProblem400([Entity(OnMissing = OnMissing.ProblemDetailsWith400)] ShapeWidget widget)
        => widget;

    [WolverineGet("/shapes/entity-miss/problem404/{id}")]
    public static ShapeWidget GetProblem404([Entity(OnMissing = OnMissing.ProblemDetailsWith404)] ShapeWidget widget)
        => widget;
}
