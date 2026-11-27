using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Alba;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.FluentValidation;
using Wolverine.Http.FluentValidation;

namespace Wolverine.Http.Tests;

/// <summary>
/// The validation middleware for the <c>[FromBody]</c> member of an <c>[AsParameters]</c> record. The body is a
/// constructor parameter, so it used to be read inside the <c>[AsParameters]</c> binding with no variable the
/// middleware could resolve, and codegen failed.
/// </summary>
public class fluent_validation_of_as_parameters_body : IAsyncLifetime
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

        builder.Services.AddSingleton<IValidator<DeclineRingCommand>, DeclineRingCommand.Validator>();
        builder.Services.AddSingleton<IValidator<DeclineRingByPropertyCommand>, DeclineRingByPropertyCommand.Validator>();
        builder.Services.AddSingleton<IValidator<DeclineRingPayload>, DeclineRingPayload.Validator>();
        builder.Services.AddSingleton<IValidator<AcceptRingPayload>, AcceptRingPayload.Validator>();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                opts.UseFluentValidationProblemDetailMiddleware();
                opts.CustomizeHttpEndpointDiscovery(q =>
                    q.Excludes.WithCondition("Not the validated [AsParameters] body endpoints",
                        type => type != typeof(FluentValidatedAsParametersBodyEndpoints) &&
                                type != typeof(LoadingAsParametersBodyEndpoints)));
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

    [Fact]
    public async Task a_valid_body_reaches_the_endpoint()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DeclineRingPayload(DateTimeOffset.UtcNow))
                .ToUrl($"/api/as-parameters-validation/ring/{Guid.NewGuid()}/decline");
            x.StatusCodeShouldBe(204);
        });
    }

    [Fact]
    public async Task the_command_validator_reports_a_body_member_by_its_path()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new DeclineRingPayload(default))
                .ToUrl($"/api/as-parameters-validation/ring/{Guid.NewGuid()}/decline");
            x.StatusCodeShouldBe(400);
        });

        var problems = await result.ReadAsJsonAsync<HttpValidationProblemDetails>();
        problems.Errors.Keys.ShouldBe(["Body.At"]);
        problems.Errors["Body.At"].ShouldHaveSingleItem();
    }

    [Fact]
    public async Task the_command_validator_reports_a_body_property_member_by_its_path()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new DeclineRingPayload(default))
                .ToUrl($"/api/as-parameters-validation/ring/{Guid.NewGuid()}/decline-by-property");
            x.StatusCodeShouldBe(400);
        });

        var problems = await result.ReadAsJsonAsync<HttpValidationProblemDetails>();
        problems.Errors.Keys.ShouldBe(["Body.At"]);
        problems.Errors["Body.At"].ShouldHaveSingleItem();
    }

    [Fact]
    public async Task the_command_validator_turns_away_a_null_body()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Text("null").ToUrl($"/api/as-parameters-validation/ring/{Guid.NewGuid()}/decline");
            x.WithRequestHeader("Content-Type", "application/json");
            x.StatusCodeShouldBe(400);
        });

        var problems = await result.ReadAsJsonAsync<HttpValidationProblemDetails>();
        problems.Errors.Keys.ShouldBe(["Body"]);
    }

    [Fact]
    public async Task a_body_validator_alone_reports_the_member_by_its_own_name()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new AcceptRingPayload(default))
                .ToUrl($"/api/as-parameters-validation/ring/{Guid.NewGuid()}/accept");
            x.StatusCodeShouldBe(400);
        });

        var problems = await result.ReadAsJsonAsync<HttpValidationProblemDetails>();
        problems.Errors.Keys.ShouldBe(["At"]);
    }

    [Fact]
    public async Task a_body_validator_alone_lets_a_valid_body_through()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new AcceptRingPayload(DateTimeOffset.UtcNow))
                .ToUrl($"/api/as-parameters-validation/ring/{Guid.NewGuid()}/accept");
            x.StatusCodeShouldBe(204);
        });
    }

    [Fact]
    public async Task a_body_bound_through_a_renamed_constructor_parameter_is_validated()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new DeclineRingPayload(default))
                .ToUrl($"/api/as-parameters-validation/ring/{Guid.NewGuid()}/decline-renamed");
            x.StatusCodeShouldBe(400);
        });

        var problems = await result.ReadAsJsonAsync<HttpValidationProblemDetails>();
        problems.Errors.Keys.ShouldBe(["At"]);
    }

    [Fact]
    public async Task an_endpoints_own_middleware_runs_after_body_validation()
    {
        var notificationId = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new AcceptRingPayload(default))
                .ToUrl($"/api/as-parameters-validation/ring/{notificationId}/accept-after-load");
            x.StatusCodeShouldBe(400);
        });

        RingLoadingMiddleware.Loaded.ShouldNotContain(notificationId);
    }
}

public class data_annotations_validation_of_as_parameters_body : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(GetType().Assembly);
        });

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                opts.UseDataAnnotationsValidationProblemDetailMiddleware();
                opts.CustomizeHttpEndpointDiscovery(q =>
                    q.Excludes.WithCondition("Not the annotated [AsParameters] body endpoints",
                        type => type != typeof(AnnotatedAsParametersBodyEndpoints)));
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

    [Fact]
    public async Task a_valid_body_reaches_the_endpoint()
    {
        await theHost.Scenario(x =>
        {
            x.Put.Json(new UpdateProfilePayload("Engineer"))
                .ToUrl($"/api/as-parameters-validation/people/{Guid.NewGuid()}/profile");
            x.StatusCodeShouldBe(204);
        });
    }

    [Fact]
    public async Task an_invalid_body_is_turned_away()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Put.Json(new UpdateProfilePayload(new string('x', 101)))
                .ToUrl($"/api/as-parameters-validation/people/{Guid.NewGuid()}/profile");
            x.StatusCodeShouldBe(400);
        });

        var problems = await result.ReadAsJsonAsync<HttpValidationProblemDetails>();
        problems.Errors.Keys.ShouldBe(["JobTitle"]);
    }

    [Fact]
    public async Task the_route_values_bind_before_the_body_is_validated()
    {
        // An unparseable route value is a 404 from the [AsParameters] binding, ahead of the body's 400
        await theHost.Scenario(x =>
        {
            x.Put.Json(new UpdateProfilePayload(new string('x', 101)))
                .ToUrl("/api/as-parameters-validation/people/not-a-guid/profile-unconstrained");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task a_body_bound_through_a_renamed_constructor_parameter_is_turned_away()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Put.Json(new UpdateProfilePayload(new string('x', 101)))
                .ToUrl($"/api/as-parameters-validation/people/{Guid.NewGuid()}/profile-renamed");
            x.StatusCodeShouldBe(400);
        });

        var problems = await result.ReadAsJsonAsync<HttpValidationProblemDetails>();
        problems.Errors.Keys.ShouldBe(["JobTitle"]);
    }

    [Fact]
    public void a_body_two_properties_could_hold_is_not_read_off_the_as_parameters_type()
    {
        var graph = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("PUT", "/api/as-parameters-validation/people/{personId:guid}/rename-ambiguous")!;

        chain.TryFindAsParametersBody(typeof(RenamePayload), out _).ShouldBeFalse();
    }

    [Fact]
    public async Task an_invalid_body_property_is_turned_away()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Put.Json(new UpdateProfilePayload(new string('x', 101)))
                .ToUrl($"/api/as-parameters-validation/people/{Guid.NewGuid()}/profile-by-property");
            x.StatusCodeShouldBe(400);
        });

        var problems = await result.ReadAsJsonAsync<HttpValidationProblemDetails>();
        problems.Errors.Keys.ShouldBe(["JobTitle"]);
    }
}

public record DeclineRingPayload(DateTimeOffset At)
{
    public class Validator : AbstractValidator<DeclineRingPayload>
    {
        public Validator() => RuleFor(x => x.At).NotEmpty();
    }
}

public record DeclineRingCommand([FromRoute] Guid NotificationId, [FromBody] DeclineRingPayload Body)
{
    public class Validator : AbstractValidator<DeclineRingCommand>
    {
        public Validator() => RuleFor(x => x.Body).NotNull().SetValidator(new DeclineRingPayload.Validator());
    }
}

public class DeclineRingByPropertyCommand
{
    [FromRoute] public Guid NotificationId { get; set; }

    [FromBody] public DeclineRingPayload Body { get; set; } = null!;

    public class Validator : AbstractValidator<DeclineRingByPropertyCommand>
    {
        public Validator() => RuleFor(x => x.Body).NotNull().SetValidator(new DeclineRingPayload.Validator());
    }
}

public record AcceptRingPayload(DateTimeOffset At)
{
    public class Validator : AbstractValidator<AcceptRingPayload>
    {
        public Validator() => RuleFor(x => x.At).NotEmpty();
    }
}

public record AcceptRingCommand([FromRoute] Guid NotificationId, [FromBody] AcceptRingPayload Body);

public static class FluentValidatedAsParametersBodyEndpoints
{
    [EmptyResponse]
    [WolverinePost("/api/as-parameters-validation/ring/{notificationId:guid}/decline")]
    public static void Decline([AsParameters] DeclineRingCommand command)
    {
    }

    [EmptyResponse]
    [WolverinePost("/api/as-parameters-validation/ring/{notificationId:guid}/decline-by-property")]
    public static void DeclineByProperty([AsParameters] DeclineRingByPropertyCommand command)
    {
    }

    [EmptyResponse]
    [WolverinePost("/api/as-parameters-validation/ring/{notificationId:guid}/accept")]
    public static void Accept([AsParameters] AcceptRingCommand command)
    {
    }

    [EmptyResponse]
    [WolverinePost("/api/as-parameters-validation/ring/{notificationId:guid}/decline-renamed")]
    public static void DeclineRenamed([AsParameters] RenamedDeclineRingCommand command)
    {
    }
}

// The body binds through a constructor parameter whose name no property shares
public class RenamedDeclineRingCommand([FromRoute] Guid notificationId, [FromBody] DeclineRingPayload payload)
{
    public Guid NotificationId { get; } = notificationId;
    public DeclineRingPayload Body { get; } = payload;
}

public static class RingLoadingMiddleware
{
    public static readonly ConcurrentQueue<Guid> Loaded = new();

    public static void Load(Guid notificationId) => Loaded.Enqueue(notificationId);
}

[Middleware(typeof(RingLoadingMiddleware))]
public static class LoadingAsParametersBodyEndpoints
{
    [EmptyResponse]
    [WolverinePost("/api/as-parameters-validation/ring/{notificationId:guid}/accept-after-load")]
    public static void Accept([AsParameters] AcceptRingCommand command)
    {
    }
}

public record UpdateProfilePayload([property: MaxLength(100)] string? JobTitle);

public record UpdateProfileCommand([FromRoute] Guid PersonId, [FromBody] UpdateProfilePayload Body);

public class UpdateProfileByPropertyCommand
{
    [FromRoute] public Guid PersonId { get; set; }

    [FromBody] public UpdateProfilePayload Body { get; set; } = null!;
}

// The body binds through a constructor parameter whose name no property shares
public class RenamedUpdateProfileCommand([FromRoute] Guid personId, [FromBody] UpdateProfilePayload payload)
{
    public Guid PersonId { get; } = personId;
    public UpdateProfilePayload Body { get; } = payload;
}

// As above, but two properties have the body's type, so neither can be told to be the body
public class AmbiguousRenameCommand([FromRoute] Guid personId, [FromBody] RenamePayload payload)
{
    public Guid PersonId { get; } = personId;
    public RenamePayload Body { get; } = payload;
    public RenamePayload Previous { get; } = payload;
}

public static class AnnotatedAsParametersBodyEndpoints
{
    [EmptyResponse]
    [WolverinePut("/api/as-parameters-validation/people/{personId:guid}/profile-renamed")]
    public static void UpdateRenamed([AsParameters] RenamedUpdateProfileCommand command)
    {
    }

    [EmptyResponse]
    [WolverinePut("/api/as-parameters-validation/people/{personId:guid}/rename-ambiguous")]
    public static void RenameAmbiguous([AsParameters] AmbiguousRenameCommand command)
    {
    }

    [EmptyResponse]
    [WolverinePut("/api/as-parameters-validation/people/{personId}/profile-unconstrained")]
    public static void UpdateUnconstrained([AsParameters] UpdateProfileCommand command)
    {
    }

    [EmptyResponse]
    [WolverinePut("/api/as-parameters-validation/people/{personId:guid}/profile-by-property")]
    public static void UpdateByProperty([AsParameters] UpdateProfileByPropertyCommand command)
    {
    }

    [EmptyResponse]
    [WolverinePut("/api/as-parameters-validation/people/{personId:guid}/profile")]
    public static void Update([AsParameters] UpdateProfileCommand command)
    {
    }
}
