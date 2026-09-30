namespace Wolverine.Http.Tests.DifferentAssembly.OpenApi;

// The 400 a Validate() refusal advertises in the generated OpenAPI document. Both conventions write a
// ProblemDetails body, so the document must say so -- not just announce a bare 400.

public record ShapeValidatedCommand(string Name);

public static class SimpleValidationShapeEndpoint
{
    public static IEnumerable<string> Validate(ShapeValidatedCommand command)
    {
        if (string.IsNullOrEmpty(command.Name)) yield return "Name is required";
    }

    [WolverinePost("/shapes/validation/simple")]
    public static string Post(ShapeValidatedCommand command) => "ok";
}

public static class RequirementResultShapeEndpoint
{
    public static RequirementResult Validate(ShapeValidatedCommand command)
        => string.IsNullOrEmpty(command.Name)
            ? new RequirementResult(HandlerContinuation.Stop, ["Name is required"])
            : RequirementResult.AllGood();

    [WolverinePost("/shapes/validation/requirement")]
    public static string Post(ShapeValidatedCommand command) => "ok";
}
