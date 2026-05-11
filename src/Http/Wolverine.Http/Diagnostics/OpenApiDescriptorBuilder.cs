using JasperFx.Descriptors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Wolverine.Http.Diagnostics;

/// <summary>
/// Pure mapping helper from ApiExplorer's <see cref="ApiDescription"/>
/// (and the matching ASP.NET Core endpoint's metadata) into the
/// wire-format-neutral
/// <see cref="OpenApiOperationDescriptor"/> shape used by
/// CritterWatch.
/// </summary>
/// <remarks>
/// Lives inside Wolverine.Http so that the bridge can run with no
/// dependency on Microsoft.AspNetCore.OpenApi. The richer schema
/// builder backed by <c>Microsoft.AspNetCore.OpenApi</c>'s
/// <c>OpenApiDocumentService</c> lives in
/// <c>Wolverine.CritterWatch.Http</c> and replaces these schemas at
/// the consumer side when both are loaded.
/// </remarks>
public static class OpenApiDescriptorBuilder
{
    /// <summary>Maximum schema-tree depth before we emit a <c>$ref</c> chip (Q12).</summary>
    public const int MaxInlineSchemaDepth = 3;

    public static OpenApiOperationDescriptor? TryBuildForWolverine(
        HttpChain chain,
        IApiDescriptionGroupCollectionProvider apiDescriptions)
    {
        // Match by route + method on the WolverineActionDescriptor's stamp.
        var route = chain.RoutePattern?.RawText;
        if (route is null) return null;

        ApiDescription? apiDescription = null;
        foreach (var group in apiDescriptions.ApiDescriptionGroups.Items)
        {
            foreach (var item in group.Items)
            {
                if (item.ActionDescriptor is WolverineActionDescriptor wad &&
                    wad.Chain == chain)
                {
                    apiDescription = item;
                    break;
                }
            }

            if (apiDescription is not null) break;
        }

        return apiDescription is null
            ? null
            : Build(apiDescription, chain.Endpoint, chain.Tags.Select(t => $"{t.Key}={t.Value}").ToList());
    }

    public static OpenApiOperationDescriptor? Build(
        ApiDescription apiDescription,
        Endpoint? endpoint,
        List<string>? defaultTags = null)
    {
        var op = new OpenApiOperationDescriptor
        {
            OperationId = apiDescription.ActionDescriptor.DisplayName ?? string.Empty,
            Tags = (apiDescription.GroupName is { } grp ? new List<string> { grp } : new List<string>())
                .Concat(defaultTags ?? new List<string>())
                .Distinct()
                .ToList()
        };

        // Parameters
        foreach (var param in apiDescription.ParameterDescriptions)
        {
            if (param.Source == BindingSource.Body || param.Source == BindingSource.Form ||
                param.Source == BindingSource.FormFile)
            {
                continue;
            }

            op.Parameters.Add(new OpenApiParameterDescriptor
            {
                Name = param.Name,
                In = mapBindingSource(param.Source),
                Required = param.IsRequired,
                Schema = SchemaFlattener.For(param.Type)
            });
        }

        // Request body
        var bodyParam = apiDescription.ParameterDescriptions.FirstOrDefault(p => p.Source == BindingSource.Body);
        if (bodyParam is not null)
        {
            var body = new OpenApiRequestBodyDescriptor { Required = bodyParam.IsRequired };
            var formats = apiDescription.SupportedRequestFormats.Select(r => r.MediaType ?? "application/json").Distinct();
            foreach (var format in formats.DefaultIfEmpty("application/json"))
            {
                body.Content[format] = new OpenApiMediaTypeDescriptor
                {
                    Schema = SchemaFlattener.For(bodyParam.Type)
                };
            }
            op.RequestBody = body;
        }
        else if (apiDescription.ParameterDescriptions.Any(p => p.Source == BindingSource.Form || p.Source == BindingSource.FormFile))
        {
            var body = new OpenApiRequestBodyDescriptor { Required = true };
            var formats = apiDescription.SupportedRequestFormats.Select(r => r.MediaType ?? "multipart/form-data").Distinct();
            foreach (var format in formats.DefaultIfEmpty("multipart/form-data"))
            {
                body.Content[format] = new OpenApiMediaTypeDescriptor
                {
                    Schema = new OpenApiSchemaDescriptor { Type = "object" }
                };
            }
            op.RequestBody = body;
        }

        // Responses
        foreach (var responseType in apiDescription.SupportedResponseTypes)
        {
            var response = new OpenApiResponseDescriptor();
            foreach (var format in responseType.ApiResponseFormats)
            {
                if (format.MediaType is null) continue;
                response.Content[format.MediaType] = new OpenApiMediaTypeDescriptor
                {
                    Schema = responseType.Type is { } t ? SchemaFlattener.For(t) : null
                };
            }
            op.Responses[responseType.StatusCode.ToString()] = response;
        }

        if (op.Responses.Count == 0)
        {
            op.Responses["200"] = new OpenApiResponseDescriptor();
        }

        // Security — surface auth schemes from endpoint metadata
        if (endpoint is not null)
        {
            foreach (var attr in endpoint.Metadata.OfType<AuthorizeAttribute>())
            {
                op.Security.Add(new OpenApiSecurityDescriptor
                {
                    SchemeName = attr.AuthenticationSchemes ?? string.Empty,
                    SchemeType = "http",
                    Scopes = string.IsNullOrEmpty(attr.Roles)
                        ? new List<string>()
                        : attr.Roles.Split(',').Select(r => r.Trim()).ToList()
                });
            }
        }

        return op;
    }

    public static (bool RequiresAuth, List<string> Policies, List<string> Roles) ReadAuthorizationFromEndpoint(Endpoint? endpoint)
    {
        if (endpoint is null) return (false, new List<string>(), new List<string>());

        var auths = endpoint.Metadata.OfType<AuthorizeAttribute>().ToArray();
        if (auths.Length == 0) return (false, new List<string>(), new List<string>());

        var policies = auths
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .Distinct()
            .ToList();

        var roles = auths
            .Where(a => !string.IsNullOrEmpty(a.Roles))
            .SelectMany(a => a.Roles!.Split(',').Select(r => r.Trim()))
            .Distinct()
            .ToList();

        return (true, policies, roles);
    }

    private static string mapBindingSource(BindingSource? source)
    {
        if (source is null) return "query";
        if (source == BindingSource.Path) return "path";
        if (source == BindingSource.Query) return "query";
        if (source == BindingSource.Header) return "header";
        return "query";
    }
}
