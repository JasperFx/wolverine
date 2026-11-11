using Microsoft.OpenApi;

namespace WolverineWebApi.TestSupport;

[AttributeUsage(AttributeTargets.Method)]
public abstract class OpenApiExpectationAttribute : Attribute
{
    public abstract void Validate(IOpenApiPathItem item, OpenApiOperation op, IOpenApiSource openApi);
}