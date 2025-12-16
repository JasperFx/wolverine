using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Http.DataAnnotationsValidation.Tests;

public class dataannotations_validation_middleware : IAsyncLifetime
{
    protected IAlbaHost theHost;

    [Fact]
    public void adds_problem_validation_to_open_api_metadata()
    {
        var endpoints = theHost.Services.GetServices<EndpointDataSource>().SelectMany(x => x.Endpoints).OfType<RouteEndpoint>()
            .ToList();

        var endpoint = endpoints.Single(x => x.RoutePattern.RawText == "/validate/account");

        var produces = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Single(x => x.Type == typeof(HttpValidationProblemDetails));
        produces.StatusCode.ShouldBe(400);
        produces.ContentTypes.Single().ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task one_validator_happy_path()
    {
        var createCustomer = new CreateAccount("accountName", "12345678");

        // Succeeds w/ a 200
        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(createCustomer).ToUrl("/validate/account");
            x.ContentTypeShouldBe("text/plain");
        });
    }

    [Fact]
    public async Task one_validator_sad_path()
    {
        var createCustomer = new CreateAccount(null, "123");

        var results = await theHost.Scenario(x =>
        {
            x.Post.Json(createCustomer).ToUrl("/validate/account");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });

        // Just proving that we have HttpValidationProblemDetails content
        // in the request
        var problems = results.ReadAsJson<HttpValidationProblemDetails>();
    }

    [Fact]
    public async Task one_validator_happy_path_on_complex_query_string_argument()
    {
        // Succeeds w/ a 200
        var result = await theHost.Scenario(x =>
        {
            x.Post.Url("/validate/account2")
                .QueryString(nameof(CreateAccount.AccountName), "name")
                .QueryString(nameof(CreateAccount.Reference), "12345678");
            x.ContentTypeShouldBe("text/plain");
        });
    }

    [Fact]
    public async Task one_validator_sad_path_on_complex_query_string_argument()
    {
        var results = await theHost.Scenario(x =>
        {
            x.Post.Url("/validate/account2")
                .QueryString(nameof(CreateAccount.Reference), "11111");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });

        // Just proving that we have HttpValidationProblemDetails content
        // in the request
        var problems = results.ReadAsJson<HttpValidationProblemDetails>();
    }

    [Fact]
    public async Task when_using_compound_handler_validation_is_called_before_load()
    {
        var results = await theHost.Scenario(x =>
        {
            x.Post.Url("/validate/account-compound");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });

        var problems = results.ReadAsJson<HttpValidationProblemDetails>();
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine();
        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                opts.UseDataAnnotationsValidationProblemDetailMiddleware();
            });
        });
    }

    public Task DisposeAsync()
    {
        return theHost.StopAsync();
    }
}