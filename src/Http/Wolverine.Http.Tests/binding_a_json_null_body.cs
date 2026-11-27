using Alba;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.FluentValidation;
using Wolverine.Http.FluentValidation;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-5976. A non-nullable body sent as JSON <c>null</c> answers 400, as an empty body does, when
/// <see cref="WolverineHttpOptions.RejectNullRequestBodies"/> is on. Minimal APIs and MVC both refuse it.
/// </summary>
public class binding_a_json_null_body : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await startHostAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Theory]
    [InlineData("/json-null/required")]
    [InlineData("/json-null/from-body")]
    [InlineData("/json-null/as-parameters/required")]
    [InlineData("/json-null/as-parameters/required-parameter")]
    public async Task a_json_null_required_body_is_a_400(string url)
    {
        var result = await theHost.Scenario(x =>
        {
            postJsonNull(x, url);
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });

        var problem = await result.ReadAsJsonAsync<ProblemDetails>();
        problem!.Title.ShouldBe("Request body is required");
        problem.Detail.ShouldBe("The request body for JsonNullPayload is required, and was JSON null.");
    }

    [Fact]
    public async Task a_json_null_required_body_never_reaches_the_validator()
    {
        await theHost.Scenario(x =>
        {
            postJsonNull(x, "/json-null/validated");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task an_empty_required_body_is_still_a_400()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Text("").ToUrl("/json-null/required");
            x.ConfigureHttpContext(c => c.Request.ContentType = "application/json");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task a_required_body_still_binds()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new JsonNullPayload("Jeremy")).ToUrl("/json-null/required");
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("Jeremy");
    }

    [Theory]
    [InlineData("/json-null/optional")]
    [InlineData("/json-null/as-parameters/optional")]
    [InlineData("/json-null/oblivious")]
    public async Task a_json_null_body_that_may_be_null_still_reaches_the_endpoint(string url)
    {
        var result = await theHost.Scenario(x =>
        {
            postJsonNull(x, url);
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("no body");
    }

    [Fact]
    public async Task a_json_null_struct_body_is_still_invalid_json()
    {
        var result = await theHost.Scenario(x =>
        {
            postJsonNull(x, "/json-null/struct");
            x.StatusCodeShouldBe(400);
        });

        (await result.ReadAsJsonAsync<ProblemDetails>())!.Title.ShouldBe("Invalid JSON format");
    }

    [Theory]
    [InlineData("/json-null/required")]
    [InlineData("/json-null/from-body")]
    [InlineData("/json-null/as-parameters/required")]
    [InlineData("/json-null/as-parameters/required-parameter")]
    public async Task turned_off_a_json_null_required_body_reaches_the_endpoint(string url)
    {
        await using var host = await startHostAsync(opts => opts.RejectNullRequestBodies = false);

        var result = await host.Scenario(x =>
        {
            postJsonNull(x, url);
            x.StatusCodeShouldBe(200);
        });

        (await result.ReadAsTextAsync()).ShouldBe("no body");
    }

    private static void postJsonNull(Scenario x, string url)
    {
        x.Post.Text("null").ToUrl(url);
        x.ConfigureHttpContext(c => c.Request.ContentType = "application/json");
    }

    private static async Task<IAlbaHost> startHostAsync(Action<WolverineHttpOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(binding_a_json_null_body).Assembly);
            opts.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);
        });

        builder.Services.AddSingleton<IValidator<JsonNullValidatedPayload>, JsonNullValidatedPayloadValidator>();
        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.UseFluentValidationProblemDetailMiddleware();
            configure?.Invoke(opts);

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the JSON null body endpoints",
                    type => type != typeof(JsonNullBodyEndpoints)));
        }));
    }
}

public record JsonNullPayload(string Name);

public record JsonNullValidatedPayload(string Name);

public class JsonNullValidatedPayloadValidator : AbstractValidator<JsonNullValidatedPayload>
{
    public JsonNullValidatedPayloadValidator() => RuleFor(x => x.Name).NotEmpty();
}

public struct JsonNullStruct
{
    public string Name { get; set; }
}

public class RequiredJsonNullBodyRequest
{
    [FromBody] public JsonNullPayload Body { get; set; } = null!;
}

public record RequiredJsonNullBodyParameterRequest([FromBody] JsonNullPayload Body);

public class OptionalJsonNullBodyRequest
{
    [FromBody] public JsonNullPayload? Body { get; set; }
}

public static class JsonNullBodyEndpoints
{
    [WolverinePost("/json-null/required")]
    public static string Required(JsonNullPayload body) => body is null ? "no body" : body.Name;

    [WolverinePost("/json-null/from-body")]
    public static string FromBody([FromBody] JsonNullPayload body) => body is null ? "no body" : body.Name;

    [WolverinePost("/json-null/validated")]
    public static string Validated(JsonNullValidatedPayload body) => body.Name;

    [WolverinePost("/json-null/as-parameters/required")]
    public static string AsParametersRequired([AsParameters] RequiredJsonNullBodyRequest request) =>
        request.Body is null ? "no body" : request.Body.Name;

    [WolverinePost("/json-null/as-parameters/required-parameter")]
    public static string AsParametersRequiredParameter([AsParameters] RequiredJsonNullBodyParameterRequest request) =>
        request.Body is null ? "no body" : request.Body.Name;

    [WolverinePost("/json-null/optional")]
    public static string Optional(JsonNullPayload? body) => body is null ? "no body" : body.Name;

    [WolverinePost("/json-null/as-parameters/optional")]
    public static string AsParametersOptional([AsParameters] OptionalJsonNullBodyRequest request) =>
        request.Body is null ? "no body" : request.Body.Name;

    [WolverinePost("/json-null/struct")]
    public static string Struct(JsonNullStruct body) => body.Name;

#nullable disable
    [WolverinePost("/json-null/oblivious")]
    public static string Oblivious(JsonNullPayload body) => body is null ? "no body" : body.Name;
#nullable restore
}
