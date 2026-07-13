using Microsoft.OpenApi.Models;
using Shouldly;

namespace WolverineWebApi.TestSupport;

/// <summary>
/// Asserts the exact number of parameters on the generated OpenAPI operation. Pair this with
/// [ExpectParameter] to lock down the operation's parameter list completely — [ExpectParameter] alone
/// only proves a parameter is present, not that a phantom or duplicated one is absent. See GH-3380.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class ExpectParameterCountAttribute : OpenApiExpectationAttribute
{
    public int Count { get; }

    public ExpectParameterCountAttribute(int count)
    {
        Count = count;
    }

    public override void Validate(OpenApiPathItem item, OpenApiOperation op, IOpenApiSource openApi)
    {
        var actual = op.Parameters?.Count ?? 0;

        actual.ShouldBe(Count, "Actual parameters: " +
                               (op.Parameters == null || op.Parameters.Count == 0
                                   ? "<none>"
                                   : string.Join(", ", op.Parameters.Select(p => $"{p.Name}({p.In})"))));
    }
}
