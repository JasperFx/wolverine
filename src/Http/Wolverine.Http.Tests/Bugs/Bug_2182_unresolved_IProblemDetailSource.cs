using Alba;
using FluentValidation;
using IntegrationTests;
using JasperFx.CodeGeneration;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Wolverine.FluentValidation;
using Wolverine.Http.FluentValidation;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_2182_unresolved_IProblemDetailSource
{
    private readonly WebApplicationBuilder _builder;

    public Bug_2182_unresolved_IProblemDetailSource()
    {
        _builder = WebApplication.CreateBuilder([]);

        _builder.Services.AddMarten(Servers.PostgresConnectionString);
        _builder.Services.DisableAllWolverineMessagePersistence();
        _builder.Services.DisableAllExternalWolverineTransports();

        _builder.Services.AddWolverineHttp();
    }

    [Fact]
    public async Task can_not_compile_with_manual_discovery_by_default()
    {
        _builder.Services.AddWolverine(ExtensionDiscovery.ManualOnly, opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(Bug_2182_Endpoint.Request).Assembly);
            opts.UseFluentValidation(); // from Wolverine.FluentValidation
            // ExtensionDiscovery.ManualMode and Wolverine.Http.FluentValidation services are not registered
        });

        await using var host = await AlbaHost.For(_builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
                opts.UseFluentValidationProblemDetailMiddleware());
        });

        // UnResolvableVariableException can be returned in 2 ways:
        // either as a failed scenario result or thrown explicitly.
        // The actual way is not important here, but the error itself is.
        var errorMessage = string.Empty;
        try
        {
            var result = await host.Scenario(x =>
            {
                x.Post.Json(new Bug_2182_Endpoint.Request("valid")).ToUrl("/Bug_2182");
                x.StatusCodeShouldBe(500);
            });
            errorMessage = await result.ReadAsTextAsync();
        }
        catch (UnResolvableVariableException ex)
        {
            errorMessage = ex.Message;
        }

        errorMessage.ShouldContain(
            "JasperFx was unable to resolve a variable of type " +
            "Wolverine.Http.FluentValidation.IProblemDetailSource<Wolverine.Http.Tests.Bugs.Bug_2182_Endpoint.Request> " +
            "as part of the method POST_Bug_2182.Handle(Microsoft.AspNetCore.Http.HttpContext httpContext)");
    }

    [Fact]
    public async Task can_compile_with_manual_extension_discovery_when_problem_detail_services_are_registered()
    {
        _builder.Services.AddWolverine(ExtensionDiscovery.ManualOnly, opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(Bug_2182_Endpoint.Request).Assembly);
            opts.UseFluentValidation(); // from Wolverine.FluentValidation
            opts.UseFluentValidationProblemDetail(); // from Wolverine.Http.FluentValidation
        });

        await using var host = await AlbaHost.For(_builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
                opts.UseFluentValidationProblemDetailMiddleware());
        });

        await host.Scenario(x =>
        {
            x.Post.Json(new Bug_2182_Endpoint.Request("valid")).ToUrl("/Bug_2182");
            x.StatusCodeShouldBe(200);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task can_compile_with_default_extension_discovery(bool useFluentValidationProblemDetail)
    {
        _builder.Services.AddWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(Bug_2182_Endpoint.Request).Assembly);
            opts.UseFluentValidation();
            if (useFluentValidationProblemDetail)
                opts.UseFluentValidationProblemDetail();
        });

        await using var host = await AlbaHost.For(_builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
                opts.UseFluentValidationProblemDetailMiddleware());
        });

        await host.Scenario(x =>
        {
            x.Post.Json(new Bug_2182_Endpoint.Request("valid")).ToUrl("/Bug_2182");
            x.StatusCodeShouldBe(200);
        });
    }

    [Theory]
    [InlineData(ExtensionDiscovery.ManualOnly, true)]
    [InlineData(ExtensionDiscovery.Automatic, true)]
    [InlineData(ExtensionDiscovery.Automatic, false)]
    public async Task can_validate_request_with_problem_detail_middleware(
      ExtensionDiscovery extensionDiscovery, bool useFluentValidationProblemDetail)
    {
        _builder.Services.AddWolverine(extensionDiscovery, opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(Bug_2182_Endpoint.Request).Assembly);
            opts.UseFluentValidation();
            if (useFluentValidationProblemDetail)
                opts.UseFluentValidationProblemDetail();
        });

        await using var host = await AlbaHost.For(_builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
                opts.UseFluentValidationProblemDetailMiddleware());
        });

        var invalidRequest = new Bug_2182_Endpoint.Request(string.Empty);
        var results = await host.Scenario(x =>
        {
            x.Post.Json(invalidRequest).ToUrl("/Bug_2182");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });
    }
}

public static class Bug_2182_Endpoint
{
    [WolverinePost("/Bug_2182")]
    public static IResult Post(Request value)
    {
        return TypedResults.Ok(value);
    }

    public record Request(string Title)
    {
        public class Validator : AbstractValidator<Request>
        {
            public Validator()
            {
                RuleFor(x => x.Title)
                  .NotEmpty();
            }
        }
    }
}