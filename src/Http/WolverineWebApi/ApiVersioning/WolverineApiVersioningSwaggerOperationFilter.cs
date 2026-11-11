using Asp.Versioning;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Globalization;
using System.Text.Json.Nodes;
using Wolverine.Http.ApiVersioning;

namespace WolverineWebApi.ApiVersioning;

/// <summary>
/// Swashbuckle operation filter that surfaces Wolverine API-versioning metadata in OpenAPI:
/// <list type="bullet">
///   <item><description>Sets <see cref="OpenApiOperation.Deprecated"/> when the chain has a deprecation policy.</description></item>
///   <item><description>Adds an <c>x-api-versioning</c> extension carrying sunset date and links.</description></item>
/// </list>
/// Copy this filter into your own project and register it via <c>opts.OperationFilter&lt;...&gt;()</c>.
/// </summary>
public sealed class WolverineApiVersioningSwaggerOperationFilter : IOperationFilter
{
    /// <inheritdoc/>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        ApplyFromMetadata(operation, metadata);
    }

    /// <summary>
    /// Core logic extracted for testability. Reads <see cref="ApiVersionEndpointHeaderState"/>
    /// from <paramref name="metadata"/> and mutates <paramref name="operation"/> accordingly.
    /// </summary>
    internal static void ApplyFromMetadata(OpenApiOperation operation, IList<object> metadata)
    {
        var state = metadata.OfType<ApiVersionEndpointHeaderState>().FirstOrDefault();
        if (state is null) return;

        if (state.Deprecation is not null)
            operation.Deprecated = true;

        if (state.Sunset is null && state.Deprecation is null) return;

        var ext = new JsonObject();

        if (state.Sunset?.Date is { } sunsetDate)
            ext["sunset"] = sunsetDate.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

        var links = new JsonArray();
        foreach (var link in (state.Sunset?.Links ?? []).Concat(state.Deprecation?.Links ?? []))
        {
            var linkObj = new JsonObject
            {
                ["href"] = link.LinkTarget?.ToString() ?? string.Empty
            };
            if (link.Title.HasValue && link.Title.Length > 0)
                linkObj["title"] = link.Title.Value;
            if (link.Type.HasValue && link.Type.Length > 0)
                linkObj["type"] = link.Type.Value;
            links.Add(linkObj);
        }
        if (links.Count > 0)
            ext["links"] = links;

        if (ext.Count > 0)
        {
            operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
            operation.Extensions["x-api-versioning"] = new JsonNodeExtension(ext);
        }
    }
}
