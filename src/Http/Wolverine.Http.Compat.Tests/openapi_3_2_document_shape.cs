#if NET11_0_OR_GREATER
using System.Text.Json;
using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Wolverine.Http.Commands;

namespace Wolverine.Http.Compat.Tests;

/// <summary>
///     GH-5147: Microsoft.AspNetCore.OpenApi 11 writes OpenAPI 3.2 documents. Wolverine's endpoints have to come
///     out of one with the same shape they have in 3.1, through both the live endpoint and the 'openapi' command's
///     document provider.
/// </summary>
public class openapi_3_2_document_shape : IAsyncLifetime
{
    private IAlbaHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await CompatHost.For(
            configureServices: services =>
            {
                services.AddOpenApi(o => o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_2);
                services.AddOpenApi("v31", o => o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1);
            },
            configureApp: app => app.MapOpenApi());
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task the_live_document()
    {
        var text = await _host.GetAsText("/openapi/v1.json");
        assertShape(text);
    }

    [Fact]
    public async Task the_openapi_command_document()
    {
        var provider = OpenApiDocumentProvider.Resolve(_host.Services);
        provider.ShouldNotBeNull();

        var writer = new StringWriter();
        await provider.GenerateAsync("v1", writer);

        assertShape(writer.ToString());
    }

    [Fact]
    public async Task every_wolverine_operation_is_described_the_same_as_in_3_1()
    {
        var v32 = await _host.GetAsText("/openapi/v1.json");
        var v31 = await _host.GetAsText("/openapi/v31.json");

        using var doc31 = JsonDocument.Parse(v31);
        doc31.RootElement.GetProperty("openapi").GetString()!.ShouldStartWith("3.1");

        var expected = operations(v31);
        expected.ShouldNotBeEmpty();
        operations(v32).ShouldBe(expected);
    }

    /// <summary>route + verb -> request content types and response codes, the shape a client is generated from</summary>
    private static string[] operations(string text)
    {
        using var document = JsonDocument.Parse(text);
        var list = new List<string>();
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var content = operation.Value.TryGetProperty("requestBody", out var body)
                    ? string.Join(",", body.GetProperty("content").EnumerateObject().Select(x => x.Name))
                    : "";
                var responses = string.Join(",",
                    operation.Value.GetProperty("responses").EnumerateObject().Select(x => x.Name));
                list.Add($"{operation.Name.ToUpperInvariant()} {path.Name} [{content}] -> {responses}");
            }
        }

        return list.OrderBy(x => x).ToArray();
    }

    private static void assertShape(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        root.GetProperty("openapi").GetString()!.ShouldStartWith("3.2");

        var paths = root.GetProperty("paths");

        var get = paths.GetProperty("/compat/csrf/get").GetProperty("get");
        get.GetProperty("responses").TryGetProperty("200", out _).ShouldBeTrue();

        var json = paths.GetProperty("/compat/csrf/json").GetProperty("post");
        json.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _)
            .ShouldBeTrue();

        var form = paths.GetProperty("/compat/csrf/form").GetProperty("post").GetProperty("requestBody")
            .GetProperty("content");
        // A lone [FromForm] scalar is described as url-encoded only, in 3.1 as much as 3.2
        form.TryGetProperty("application/x-www-form-urlencoded", out _).ShouldBeTrue();

        var file = paths.GetProperty("/compat/csrf/file").GetProperty("post").GetProperty("requestBody")
            .GetProperty("content");
        file.TryGetProperty("multipart/form-data", out _).ShouldBeTrue();

        // DataAnnotations middleware advertises its validation problem response
        var validated = paths.GetProperty("/compat/validation/sync").GetProperty("post").GetProperty("responses");
        validated.TryGetProperty("400", out _).ShouldBeTrue();
    }
}
#endif
