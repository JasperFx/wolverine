using System.ComponentModel.DataAnnotations;
using Alba;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.FluentValidation;
using Wolverine.Http.FluentValidation;

namespace Wolverine.Http.Tests;

/// <summary>
/// An optional body that is null, because the request has none (GH-4935) or because the JSON body is <c>null</c>,
/// skips the validation middleware, as it does in ASP.NET Core MVC and minimal APIs.
/// </summary>
public class validation_of_a_null_body : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(GetType().Assembly);
            opts.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);
        });

        builder.Services.AddSingleton<IValidator<FluentValidatedPayload>, FluentValidatedPayloadValidator>();
        builder.Services.AddSingleton<IValidator<TwiceValidatedPayload>, TwiceValidatedPayloadNameValidator>();
        builder.Services.AddSingleton<IValidator<TwiceValidatedPayload>, TwiceValidatedPayloadAtValidator>();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                opts.UseFluentValidationProblemDetailMiddleware();
                opts.UseDataAnnotationsValidationProblemDetailMiddleware();

                opts.CustomizeHttpEndpointDiscovery(q =>
                    q.Excludes.WithCondition("Not the null body validation endpoints",
                        type => type != typeof(NullBodyValidationEndpoints)));
            });
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }
    }

    [Theory]
    [InlineData("/null-body/fluent/one")]
    [InlineData("/null-body/fluent/many")]
    [InlineData("/null-body/data-annotations")]
    public async Task an_absent_optional_body_skips_validation(string url)
    {
        await theHost.Scenario(x =>
        {
            // An Alba scenario always says a request can have a body, so a bodiless POST says it has none
            x.Post.Url(url);
            x.ConfigureHttpContext(c => c.Request.ContentLength = 0);
            x.StatusCodeShouldBe(204);
        });
    }

    [Theory]
    [InlineData("/null-body/fluent/one")]
    [InlineData("/null-body/fluent/many")]
    [InlineData("/null-body/data-annotations")]
    public async Task a_json_null_optional_body_skips_validation(string url)
    {
        await theHost.Scenario(x =>
        {
            x.Post.Text("null").ToUrl(url);
            x.ConfigureHttpContext(c => c.Request.ContentType = "application/json");
            x.StatusCodeShouldBe(204);
        });
    }

    [Fact]
    public async Task an_invalid_body_is_still_a_validation_problem_with_one_validator()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new FluentValidatedPayload(default)).ToUrl("/null-body/fluent/one");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task an_invalid_body_is_still_a_validation_problem_with_many_validators()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new TwiceValidatedPayload(null!, DateTimeOffset.UtcNow)).ToUrl("/null-body/fluent/many");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task an_invalid_body_is_still_a_validation_problem_with_data_annotations()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new AnnotatedPayload(null!)).ToUrl("/null-body/data-annotations");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task an_absent_required_body_is_still_refused_by_binding()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Url("/null-body/fluent/required");
            x.ConfigureHttpContext(c => c.Request.ContentLength = 0);
            x.StatusCodeShouldBe(400);
        });
    }
}

public record FluentValidatedPayload(DateTimeOffset At);

public class FluentValidatedPayloadValidator : AbstractValidator<FluentValidatedPayload>
{
    public FluentValidatedPayloadValidator() => RuleFor(x => x.At).NotEmpty();
}

public record TwiceValidatedPayload(string Name, DateTimeOffset At);

public class TwiceValidatedPayloadNameValidator : AbstractValidator<TwiceValidatedPayload>
{
    public TwiceValidatedPayloadNameValidator() => RuleFor(x => x.Name).NotEmpty();
}

public class TwiceValidatedPayloadAtValidator : AbstractValidator<TwiceValidatedPayload>
{
    public TwiceValidatedPayloadAtValidator() => RuleFor(x => x.At).NotEmpty();
}

public record AnnotatedPayload([property: Required] string Name);

public static class NullBodyValidationEndpoints
{
    [WolverinePost("/null-body/fluent/one"), EmptyResponse]
    public static void PostOne(FluentValidatedPayload? body) { }

    [WolverinePost("/null-body/fluent/many"), EmptyResponse]
    public static void PostMany(TwiceValidatedPayload? body) { }

    [WolverinePost("/null-body/data-annotations"), EmptyResponse]
    public static void PostAnnotated(AnnotatedPayload? body) { }

    [WolverinePost("/null-body/fluent/required"), EmptyResponse]
    public static void PostRequired(FluentValidatedPayload body) { }
}
