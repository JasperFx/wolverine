using Microsoft.OpenApi.Models;
using Shouldly;

namespace WolverineWebApi.TestSupport;

/// <summary>
/// Asserts the generated OpenAPI operation has no request body (e.g. a pure query/route GET, or an
/// [AsParameters] endpoint whose members are all query/header/route bound). See GH-3135.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class ExpectNoRequestBodyAttribute : OpenApiExpectationAttribute
{
    public override void Validate(OpenApiPathItem item, OpenApiOperation op, IOpenApiSource openApi)
    {
        var hasContent = op.RequestBody?.Content?.Count > 0;
        hasContent.ShouldBeFalse(
            "Expected no request body, but found content types: " +
            (op.RequestBody?.Content == null ? "<none>" : string.Join(", ", op.RequestBody.Content.Keys)));
    }
}
