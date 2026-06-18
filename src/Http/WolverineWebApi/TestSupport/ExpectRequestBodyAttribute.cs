using Microsoft.OpenApi.Models;
using Shouldly;

namespace WolverineWebApi.TestSupport;

/// <summary>
/// Asserts the request-body shape of the generated OpenAPI operation: the content type, the
/// properties that must be present, and (via <see cref="ForbiddenProperties"/>) properties that must
/// be absent — e.g. proving an [AsParameters] container type is decomposed rather than dumped whole
/// into the body, or that a route-bound property is not duplicated in the body. See GH-3135.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class ExpectRequestBodyAttribute : OpenApiExpectationAttribute
{
    public string ContentType { get; }

    /// <summary>Property names that must be present on the (ref-resolved) body schema.</summary>
    public string[] Properties { get; }

    /// <summary>Property names that must NOT be present on the body schema.</summary>
    public string[] ForbiddenProperties { get; set; } = [];

    /// <summary>When set, asserts <c>requestBody.required</c> equals this value. Unset = don't assert.</summary>
    public bool RequiredSet { get; private set; }
    private bool _required;

    public bool Required
    {
        get => _required;
        set
        {
            _required = value;
            RequiredSet = true;
        }
    }

    public ExpectRequestBodyAttribute(string contentType, params string[] properties)
    {
        ContentType = contentType;
        Properties = properties;
    }

    public override void Validate(OpenApiPathItem item, OpenApiOperation op, IOpenApiSource openApi)
    {
        op.RequestBody.ShouldNotBeNull("Expected a request body, but the operation has none");
        op.RequestBody.Content.Keys.ShouldContain(ContentType,
            $"Expected request body content type '{ContentType}'. Actual: {string.Join(", ", op.RequestBody.Content.Keys)}");

        if (RequiredSet)
        {
            op.RequestBody.Required.ShouldBe(Required, $"requestBody.required for '{ContentType}'");
        }

        var schema = OpenApiSchemaResolver.Resolve(op.RequestBody.Content[ContentType].Schema, openApi.GetOpenApiDocument());
        schema.ShouldNotBeNull($"Request body for '{ContentType}' has no schema");

        var propertyNames = OpenApiSchemaResolver.PropertyNames(schema, openApi.GetOpenApiDocument());

        foreach (var property in Properties)
        {
            propertyNames.ShouldContain(property,
                $"Expected body property '{property}'. Actual: {string.Join(", ", propertyNames)}");
        }

        foreach (var forbidden in ForbiddenProperties)
        {
            propertyNames.ShouldNotContain(forbidden,
                $"Body should NOT contain property '{forbidden}'");
        }
    }
}
