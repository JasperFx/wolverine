using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Tests.DifferentAssembly.OpenApi;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

/// <summary>
/// OpenAPI *shape* coverage: renders the real Microsoft.AspNetCore.OpenApi document for a host built on
/// the database-free "DifferentAssembly" endpoints and asserts on what actually lands in the document —
/// the operation's <c>parameters</c> and <c>requestBody</c>, not just that the route shows up.
///
/// This is the harness that GH-3380 (and the GH-3135 audit before it) said was missing: OpenAPI metadata
/// used to be derived from the endpoint method signature, so anything bound elsewhere in the chain (a
/// compound handler's Load/LoadAsync/Before, an After postprocessor, middleware from a policy) could go
/// undeclared without a single test noticing.
///
/// TO ADD A SHAPE:
///   1. Add an endpoint to Wolverine.Http.Tests.DifferentAssembly/OpenApiShapeEndpoints.cs
///      (keep it free of database/broker dependencies so the document renders with no infrastructure).
///   2. Add a [Fact] below using the ParametersFor / RequestBodyPropertiesFor / HasRequestBody helpers.
///
/// The Swashbuckle half of this coverage lives in WolverineWebApi as attribute-driven expectations
/// ([ExpectParameter] / [ExpectRequestBody] / [ExpectNoRequestBody], validated by
/// open_api_generation.verify_open_api_expectations), so the two OpenAPI stacks Wolverine supports are
/// both exercised.
/// </summary>
public class OpenApiShapeFixture : IAsyncLifetime
{
    public JsonDocument Document { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Host.UseWolverine(opts =>
        {
            // Pin endpoint discovery to the isolated, infrastructure-free assembly
            opts.ApplicationAssembly = typeof(CompoundRouteOnlyEndpoint).Assembly;
        });

        // The GH-3420 aggregate shapes need Marten to answer "what is this aggregate's id type?" while the
        // chains are built. Point it at an unreachable database (nothing listens on port 9999) so this
        // fixture stays infrastructure-free by construction: if rendering the document ever opened a
        // connection, every test in here would fail rather than quietly depend on a running Postgres.
        builder.Services.AddMarten(opts =>
        {
            opts.Connection(
                "Host=localhost;Port=9999;Database=does_not_exist;Username=nobody;Password=nobody;Timeout=2;Command Timeout=2");
        }).IntegrateWithWolverine();

        builder.Services.AddOpenApi();
        builder.Services.AddWolverineHttp();

        await using var app = builder.Build();
        app.MapWolverineEndpoints();

        // Generate the document exactly like the `openapi` command does — no host start required
        var documentProvider = OpenApiCommand.PrepareDocumentProvider(app);
        documentProvider.ShouldNotBeNull();

        var writer = new StringWriter();
        await documentProvider!.GenerateAsync("v1", writer);

        Document = JsonDocument.Parse(writer.ToString());
    }

    public Task DisposeAsync()
    {
        Document?.Dispose();
        return Task.CompletedTask;
    }
}

public record ParameterShape(string Name, string In, bool Required, string? Type, string? Format);

public class openapi_shape_tests : IClassFixture<OpenApiShapeFixture>
{
    private readonly OpenApiShapeFixture _fixture;

    public openapi_shape_tests(OpenApiShapeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void compound_handler_route_values_are_declared_as_required_path_parameters()
    {
        // GH-3380: both ids are bound by LoadAsync and appear nowhere in the endpoint method signature
        ParametersFor("/shapes/orders/{orderId}/order-lines/{orderLineId}", "get")
            .ShouldBe([
                new ParameterShape("orderId", "path", true, "integer", "int64"),
                new ParameterShape("orderLineId", "path", true, "integer", "int64")
            ]);

        HasRequestBody("/shapes/orders/{orderId}/order-lines/{orderLineId}", "get").ShouldBeFalse();
    }

    [Fact]
    public void compound_handler_types_an_unconstrained_route_value_from_the_binding_chain()
    {
        // No route constraint to fall back on: the type can only come from the Before() argument
        ParametersFor("/shapes/unconstrained/orders/{orderId}", "get")
            .ShouldBe([new ParameterShape("orderId", "path", true, "integer", "int64")]);
    }

    [Fact]
    public void compound_handler_honors_a_renamed_route_binding()
    {
        ParametersFor("/shapes/renamed/orders/{order-id}", "get")
            .ShouldBe([new ParameterShape("order-id", "path", true, "integer", "int64")]);
    }

    [Fact]
    public void query_and_header_values_bound_only_by_a_compound_handler_are_declared()
    {
        ParametersFor("/shapes/compound-query-header", "get")
            .ShouldBe([
                new ParameterShape("name", "query", false, "string", null),
                new ParameterShape("x-tenant", "header", false, "string", null)
            ]);
    }

    [Fact]
    public void query_values_bound_only_by_a_postprocessor_are_declared()
    {
        ParametersFor("/shapes/postprocessor/{orderId}", "get")
            .ShouldBe([
                new ParameterShape("orderId", "path", true, "integer", "int64"),
                new ParameterShape("audit", "query", false, "string", null)
            ]);
    }

    [Fact]
    public void plain_handler_signature_route_and_query_bindings()
    {
        ParametersFor("/shapes/plain/orders/{orderId}", "get")
            .ShouldBe([
                new ParameterShape("orderId", "path", true, "integer", "int64"),
                new ParameterShape("filter", "query", false, "string", null)
            ]);
    }

    [Fact]
    public void request_body_alongside_a_compound_handler_route_binding()
    {
        ParametersFor("/shapes/body/orders/{orderId}/order-lines", "post")
            .ShouldBe([new ParameterShape("orderId", "path", true, "integer", "int64")]);

        HasRequestBody("/shapes/body/orders/{orderId}/order-lines", "post").ShouldBeTrue();

        RequestBodyPropertiesFor("/shapes/body/orders/{orderId}/order-lines", "post", "application/json")
            .ShouldBe(["description", "quantity"], ignoreOrder: true);
    }

    [Fact]
    public void unbound_route_values_are_still_declared_and_fall_back_to_string()
    {
        ParametersFor("/shapes/unbound/{code}", "get")
            .ShouldBe([new ParameterShape("code", "path", true, "string", null)]);
    }

    [Fact]
    public void complex_query_string_type_is_declared_only_through_its_flattened_members()
    {
        ParametersFor("/shapes/complex-query", "get")
            .ShouldBe([
                new ParameterShape("filter", "query", false, "string", null),
                new ParameterShape("pageNumber", "query", false, "integer", "int32"),
                new ParameterShape("PageSize", "query", false, "integer", "int32")
            ]);

        // The container has no wire representation, so it has no business in components/schemas either
        SchemaNames().ShouldNotContain(nameof(OrderSearchQuery));
    }

    [Fact]
    public void complex_query_string_type_alongside_a_compound_handler_query_value()
    {
        ParametersFor("/shapes/complex-query-compound", "get")
            .ShouldBe([
                new ParameterShape("filter", "query", false, "string", null),
                new ParameterShape("pageNumber", "query", false, "integer", "int32"),
                new ParameterShape("PageSize", "query", false, "integer", "int32"),
                new ParameterShape("audit", "query", false, "string", null)
            ]);
    }

    [Fact]
    public void as_parameters_route_and_query_members()
    {
        ParametersFor("/shapes/asparameters/orders/{orderId}", "get")
            .ShouldBe([
                new ParameterShape("orderId", "path", true, "integer", "int64"),
                new ParameterShape("Filter", "query", false, "string", null)
            ]);

        HasRequestBody("/shapes/asparameters/orders/{orderId}", "get").ShouldBeFalse();
    }

    [Fact]
    public void as_parameters_container_sharing_a_route_value_with_a_compound_handler()
    {
        // The route value is bound twice (the [AsParameters] member and LoadAsync), but the parameter is
        // declared exactly once
        ParametersFor("/shapes/asparameters-compound/orders/{order-id}", "get")
            .ShouldBe([
                new ParameterShape("order-id", "path", true, "integer", "int64"),
                new ParameterShape("Filter", "query", false, "string", null)
            ]);
    }

    [Fact]
    public void marten_aggregate_handler_types_an_unconstrained_route_id_from_the_aggregate()
    {
        // GH-3420: {id} carries no route constraint and appears nowhere in the endpoint signature. Its type
        // is the Order aggregate's identity, which only Marten knows about.
        ParametersFor("/shapes/marten/orders/{id}/confirm", "post")
            .ShouldBe([new ParameterShape("id", "path", true, "string", "uuid")]);

        HasRequestBody("/shapes/marten/orders/{id}/confirm", "post").ShouldBeTrue();
    }

    [Fact]
    public void marten_aggregate_handler_types_the_conventional_aggregate_id_route_token()
    {
        ParametersFor("/shapes/marten/orders/{shapeOrderId}/confirm-by-convention", "post")
            .ShouldBe([new ParameterShape("shapeOrderId", "path", true, "string", "uuid")]);
    }

    [Fact]
    public void write_aggregate_binds_and_types_the_route_id_itself()
    {
        ParametersFor("/shapes/marten/orders/{id}/ship", "post")
            .ShouldBe([new ParameterShape("id", "path", true, "string", "uuid")]);
    }

    #region harness helpers

    private JsonElement operationFor(string path, string httpMethod)
    {
        var paths = _fixture.Document.RootElement.GetProperty("paths");

        paths.TryGetProperty(path, out var pathItem).ShouldBeTrue(
            $"No path '{path}' in the generated document. Known paths: " +
            string.Join(", ", paths.EnumerateObject().Select(x => x.Name)));

        pathItem.TryGetProperty(httpMethod, out var operation).ShouldBeTrue(
            $"No '{httpMethod}' operation on path '{path}'");

        return operation;
    }

    /// <summary>
    /// The rendered <c>parameters</c> of an operation, in document order.
    /// </summary>
    public IReadOnlyList<ParameterShape> ParametersFor(string path, string httpMethod)
    {
        var operation = operationFor(path, httpMethod);

        if (!operation.TryGetProperty("parameters", out var parameters))
        {
            return [];
        }

        return parameters.EnumerateArray().Select(x =>
        {
            string? type = null;
            string? format = null;

            if (x.TryGetProperty("schema", out var schema))
            {
                type = schema.TryGetProperty("type", out var t) ? t.GetString() : null;
                format = schema.TryGetProperty("format", out var f) ? f.GetString() : null;
            }

            var required = x.TryGetProperty("required", out var r) && r.GetBoolean();

            return new ParameterShape(x.GetProperty("name").GetString()!, x.GetProperty("in").GetString()!,
                required, type, format);
        }).ToList();
    }

    public bool HasRequestBody(string path, string httpMethod)
    {
        var operation = operationFor(path, httpMethod);
        return operation.TryGetProperty("requestBody", out var body) &&
               body.TryGetProperty("content", out var content) &&
               content.EnumerateObject().Any();
    }

    /// <summary>
    /// The property names of the request body schema for a content type, resolving a top level $ref
    /// against the document's components.
    /// </summary>
    public IReadOnlyList<string> RequestBodyPropertiesFor(string path, string httpMethod, string contentType)
    {
        var operation = operationFor(path, httpMethod);

        operation.TryGetProperty("requestBody", out var requestBody).ShouldBeTrue(
            $"Operation {httpMethod} {path} has no request body");

        var content = requestBody.GetProperty("content");
        content.TryGetProperty(contentType, out var media).ShouldBeTrue(
            $"No '{contentType}' content on the request body of {httpMethod} {path}");

        var schema = media.GetProperty("schema");
        schema = resolveSchema(schema);

        return schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(x => x.Name).ToList()
            : [];
    }

    /// <summary>
    /// Every type name registered under <c>components/schemas</c> in the rendered document.
    /// </summary>
    public IReadOnlyList<string> SchemaNames()
    {
        var root = _fixture.Document.RootElement;

        if (!root.TryGetProperty("components", out var components) ||
            !components.TryGetProperty("schemas", out var schemas))
        {
            return [];
        }

        return schemas.EnumerateObject().Select(x => x.Name).ToList();
    }

    private JsonElement resolveSchema(JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var id = reference.GetString()!.Split('/').Last();
            return _fixture.Document.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty(id);
        }

        return schema;
    }

    #endregion
}
