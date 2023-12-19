using Microsoft.OpenApi.Models;

namespace WolverineWebApi.TestSupport;

[AttributeUsage(AttributeTargets.Method)]
public abstract class OpenApiExpectationAttribute : Attribute
{
    public abstract void Validate(OpenApiPathItem item, OpenApiOperation op, IOpenApiSource openApi);
}