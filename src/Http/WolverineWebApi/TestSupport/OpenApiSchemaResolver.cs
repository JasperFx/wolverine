using Microsoft.OpenApi.Models;

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
    public static OpenApiSchema? Resolve(OpenApiSchema? schema, OpenApiDocument document)
    {
        if (schema?.Reference?.Id is string id &&
            document.Components?.Schemas?.TryGetValue(id, out var resolved) == true)
        {
            return resolved;
        }

        return schema;
    }

    /// <summary>
    /// The effective set of property names declared by a schema, following a top-level <c>$ref</c>
    /// and flattening one level of <c>allOf</c> composition (which Swashbuckle uses for form bodies).
    /// </summary>
    public static IReadOnlyList<string> PropertyNames(OpenApiSchema? schema, OpenApiDocument document)
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
}
