using Microsoft.OpenApi.Models;
using Shouldly;

namespace WolverineWebApi.TestSupport;

/// <summary>
/// Asserts that the generated OpenAPI operation exposes a single request parameter with the given
/// name and location, and (optionally) the expected schema <c>type</c>/<c>format</c>. Drives the
/// GH-3135 coverage of route/query/header parameter fidelity (e.g. {id:guid} -> uuid).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class ExpectParameterAttribute : OpenApiExpectationAttribute
{
    public string Name { get; }
    public ParameterLocation In { get; }

    /// <summary>Expected schema <c>type</c> (e.g. "string", "integer", "boolean"). Null = don't assert.</summary>
    public string? Type { get; set; }

    /// <summary>Expected schema <c>format</c> (e.g. "uuid", "int32", "date-time"). Null = don't assert.</summary>
    public string? Format { get; set; }

    public ExpectParameterAttribute(string name, ParameterLocation @in)
    {
        Name = name;
        In = @in;
    }

    public override void Validate(OpenApiPathItem item, OpenApiOperation op, IOpenApiSource openApi)
    {
        var parameter = op.Parameters?.FirstOrDefault(x => x.Name == Name && x.In == In);
        parameter.ShouldNotBeNull($"Expected a parameter '{Name}' in '{In}'. Actual parameters: " +
                                  (op.Parameters == null
                                      ? "<none>"
                                      : string.Join(", ", op.Parameters.Select(p => $"{p.Name}({p.In})"))));

        if (Type != null)
        {
            parameter.Schema.ShouldNotBeNull($"Parameter '{Name}' has no schema");
            parameter.Schema.Type.ShouldBe(Type, $"Parameter '{Name}' schema type");
        }

        if (Format != null)
        {
            parameter.Schema.ShouldNotBeNull($"Parameter '{Name}' has no schema");
            parameter.Schema.Format.ShouldBe(Format, $"Parameter '{Name}' schema format");
        }
    }
}
