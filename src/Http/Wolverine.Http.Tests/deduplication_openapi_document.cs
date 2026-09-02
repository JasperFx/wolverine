using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Shouldly;
using Swashbuckle.AspNetCore.Swagger;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-4180 follow-up: the deduplication refusal codes as they appear in the <b>generated OpenAPI
/// document</b>, not merely in the endpoint metadata that feeds it.
///
/// <para>
/// The existing coverage asserts on <c>IProducesResponseTypeMetadata</c>, which is the input to
/// document generation rather than its output. That is not the same claim: Swashbuckle can drop or
/// reshape a response, and <c>registerDeduplicationMetadata</c> deliberately emits two different
/// shapes -- a bare <c>Produces(status)</c> for a 2xx, and <c>Produces(status,
/// "application/problem+json")</c> for a refusal. Nothing proved the problem-details content type
/// survived into the document, or that a benign 2xx really did arrive without one.
/// </para>
///
/// <para>
/// The host below deliberately sets an application-wide default of 422 so the same fixture also
/// proves the document and the runtime agree about a duplicate -- a disagreement there is a contract
/// lie that no request-level test would catch.
/// </para>
/// </summary>
public class deduplication_openapi_document : IAsyncLifetime
{
    private const string ProblemDetails = "application/problem+json";

    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "http_dedup_openapi");

            opts.Durability.EnableMessageDeduplication = true;
            opts.Durability.DeduplicationWindow = 1.Hours();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(deduplication_openapi_document).Assembly);
        });

        builder.Services.AddWolverineHttp();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(x =>
            x.SwaggerDoc("default", new OpenApiInfo { Title = "Dedup", Version = "default" }));

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.DefaultDuplicateStatusCode = 422;

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a deduplication OpenAPI test endpoint",
                    type => type != typeof(ApiInheritsDefaultEndpoint)
                            && type != typeof(ApiExplicitConflictEndpoint)
                            && type != typeof(ApiBenignReplayEndpoint)));
        }));

        await ((IHost)theHost).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    private OpenApiOperation operationFor(string path)
    {
        var swagger = theHost.Services.GetRequiredService<ISwaggerProvider>();
        var document = swagger.GetSwagger("default");

        document.Paths.TryGetValue(path, out var item)
            .ShouldBeTrue($"The generated OpenAPI document has no path for {path}");

        return item.Operations[OperationType.Post];
    }

    [Fact]
    public void the_duplicate_refusal_is_a_response_in_the_generated_document()
    {
        var operation = operationFor("/dedup-api/inherits");

        operation.Responses.ContainsKey("422")
            .ShouldBeTrue("the duplicate refusal must appear as a response in the OpenAPI document");

        operation.Responses["422"].Content.ContainsKey(ProblemDetails)
            .ShouldBeTrue("a refusal is written as a problem document, and the document must say so");
    }

    [Fact]
    public void the_missing_key_refusal_is_a_response_in_the_generated_document()
    {
        var operation = operationFor("/dedup-api/inherits");

        operation.Responses.ContainsKey("400")
            .ShouldBeTrue("the missing-key refusal must appear as a response in the OpenAPI document");

        operation.Responses["400"].Content.ContainsKey(ProblemDetails).ShouldBeTrue();
    }

    [Fact]
    public void an_explicit_status_survives_into_the_generated_document()
    {
        var operation = operationFor("/dedup-api/explicit");

        operation.Responses.ContainsKey("409")
            .ShouldBeTrue("an endpoint that stated 409 must advertise 409");
        operation.Responses.ContainsKey("422")
            .ShouldBeFalse("the application default must not appear on an endpoint that overrode it");
    }

    [Fact]
    public void a_benign_replay_is_advertised_without_a_problem_document()
    {
        var operation = operationFor("/dedup-api/benign");

        operation.Responses.ContainsKey("204")
            .ShouldBeTrue("a benign replay status must still be discoverable");

        // The half that metadata assertions could not reach. A 2xx is emitted as a bare status
        // deliberately -- a problem document describing a response the application has declared benign
        // would be actively wrong -- and this is what proves the document agrees.
        operation.Responses["204"].Content.ContainsKey(ProblemDetails)
            .ShouldBeFalse("a benign replay returns no body, so it must not advertise problem details");
    }

    [Fact]
    public async Task the_document_and_the_runtime_agree_about_a_duplicate()
    {
        // The point of the whole exercise: an OpenAPI document promising one code while the endpoint
        // answers another is a contract lie, and neither a metadata test nor a request test can catch
        // it alone.
        var key = Guid.NewGuid().ToString();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("first")).ToUrl("/dedup-api/inherits");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBeOk();
        });

        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("replay")).ToUrl("/dedup-api/inherits");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(422);
        });

        var advertised = operationFor("/dedup-api/inherits").Responses.Keys;

        advertised.ShouldContain(result.Context.Response.StatusCode.ToString());
    }
}

public static class ApiInheritsDefaultEndpoint
{
    [Deduplicated]
    [WolverinePost("/dedup-api/inherits")]
    public static string Post(DedupRequest request) => "ok";
}

public static class ApiExplicitConflictEndpoint
{
    [Deduplicated(DuplicateStatusCode = 409)]
    [WolverinePost("/dedup-api/explicit")]
    public static string Post(DedupRequest request) => "ok";
}

public static class ApiBenignReplayEndpoint
{
    [Deduplicated(DuplicateStatusCode = 204)]
    [WolverinePost("/dedup-api/benign")]
    public static string Post(DedupRequest request) => "ok";
}
