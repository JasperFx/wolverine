using Microsoft.OpenApi;

namespace WolverineWebApi.TestSupport;

/// <summary>
/// Helpers for navigating a generated OpenAPI document's schemas — resolving <c>$ref</c> indirection
/// against <see cref="OpenApiDocument.Components"/> and flattening composed (<c>allOf</c>) schemas — so
/// request-body expectations can assert against effective property sets. See GH-3135.
/// </summary>
public static class OpenApiSchemaResolver
{
    /// <summary>
    /// Resolve a schema that may be a bare <c>$ref</c> into its concrete component schema.
    /// </summary>
    public static IOpenApiSchema? Resolve(IOpenApiSchema? schema, OpenApiDocument document)
    {
        if (schema is OpenApiSchemaReference reference)
        {
            if (reference.Reference?.Id is string id &&
                document.Components?.Schemas?.TryGetValue(id, out var resolved) == true)
            {
                return resolved;
            }

            return reference.Target ?? schema;
        }

        return schema;
    }

    /// <summary>
    /// The effective set of property names declared by a schema, following a top-level <c>$ref</c>
    /// and flattening one level of <c>allOf</c> composition (which Swashbuckle uses for form bodies).
    /// </summary>
    public static IReadOnlyList<string> PropertyNames(IOpenApiSchema? schema, OpenApiDocument document)
    {
        var resolved = Resolve(schema, document);
        if (resolved == null)
        {
            return [];
        }

        var names = new List<string>();

        if (resolved.Properties != null)
        {
            names.AddRange(resolved.Properties.Keys);
        }

        if (resolved.AllOf != null)
        {
            foreach (var part in resolved.AllOf)
            {
                names.AddRange(PropertyNames(part, document));
            }
        }

        return names;
    }

    /// <summary>
    /// The schema's <c>type</c> as the lower-case JSON Schema keyword ("string", "integer", ...), with the
    /// <c>null</c> flag that marks a nullable type in OpenAPI 3.1 left out.
    /// </summary>
    public static string? TypeName(IOpenApiSchema? schema)
    {
        if (schema?.Type is not { } type)
        {
            return null;
        }

        var withoutNull = type & ~JsonSchemaType.Null;
        return withoutNull == 0 ? "null" : withoutNull.ToString().ToLowerInvariant();
    }
}
