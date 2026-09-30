using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Shouldly;
using Swashbuckle.AspNetCore.Swagger;
using Wolverine.Http.Tests.DifferentAssembly.OpenApi;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

/// <summary>
/// The Swashbuckle half of the [Entity] miss and Validate() refusal coverage in <see cref="openapi_shape_tests" />, asserting on the
/// document Swashbuckle actually generates rather than the endpoint metadata that feeds it.
///
/// <para>
/// A problem-details miss used to be registered as <c>Produces(status, "application/problem+json")</c>.
/// Without a response TYPE, Swashbuckle emits the status with no content at all -- the same defect #4252
/// fixed for deduplication refusals -- so the generated spec promised a 404 while saying nothing about the
/// ProblemDetails body the endpoint really writes.
/// </para>
/// </summary>
public class entity_miss_swashbuckle_document : IAsyncLifetime
{
    private const string ProblemJson = "application/problem+json";

    private WebApplication theApp = null!;
    private OpenApiDocument theDocument = null!;

    public ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        // The DifferentAssembly endpoints need Marten to build their chains. Point it at an unreachable
        // database (nothing listens on port 9999): the host is never started, so nothing connects.
        builder.Services.AddMarten(opts =>
        {
            opts.Connection(
                "Host=localhost;Port=9999;Database=does_not_exist;Username=nobody;Password=nobody;Timeout=2;Command Timeout=2");
        }).IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(EntityMissShapeEndpoints).Assembly;
        });

        builder.Services.AddWolverineHttp();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(x =>
            x.SwaggerDoc("default", new OpenApiInfo { Title = "Entity misses", Version = "default" }));

        theApp = builder.Build();
        theApp.MapWolverineEndpoints();

        theDocument = theApp.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("default");

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await theApp.DisposeAsync();
    }

    private OpenApiResponse responseFor(string path, string status, OperationType method = OperationType.Get)
    {
        theDocument.Paths.TryGetValue(path, out var item)
            .ShouldBeTrue($"The generated OpenAPI document has no path for {path}");

        var responses = item.Operations[method].Responses;
        responses.TryGetValue(status, out var response)
            .ShouldBeTrue($"{path} advertises no {status} response. Known: {string.Join(", ", responses.Keys)}");

        return response;
    }

    [Theory]
    [InlineData("/shapes/entity-miss/problem404/{id}", "404")]
    [InlineData("/shapes/entity-miss/problem400/{id}", "400")]
    public void a_problem_details_entity_miss_advertises_its_problem_document(string path, string status)
    {
        var response = responseFor(path, status);

        response.Content.TryGetValue(ProblemJson, out var media)
            .ShouldBeTrue("the miss is written as a problem document, and the generated document must say so");

        media.Schema.Reference?.Id.ShouldBe(nameof(Microsoft.AspNetCore.Mvc.ProblemDetails));
    }

    [Theory]
    [InlineData("/shapes/validation/simple")]
    [InlineData("/shapes/validation/requirement")]
    public void a_validate_refusal_advertises_its_problem_document(string path)
    {
        var response = responseFor(path, "400", OperationType.Post);

        response.Content.TryGetValue(ProblemJson, out var media)
            .ShouldBeTrue("a Validate() refusal is written as a problem document, and the generated document must say so");

        media.Schema.Reference?.Id.ShouldBe(nameof(Microsoft.AspNetCore.Mvc.ProblemDetails));
    }

    [Fact]
    public void a_simple_404_entity_miss_advertises_no_body()
    {
        responseFor("/shapes/entity-miss/simple404/{id}", "404").Content
            .ShouldBeEmpty("a Simple404 writes no body, so it must not advertise one");
    }
}
