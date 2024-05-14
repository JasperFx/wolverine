using Alba;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class problem_details_usage_in_http_middleware : IntegrationContext
{
    public problem_details_usage_in_http_middleware(AppFixture fixture) : base(fixture)
    {
    }

    #region sample_testing_problem_details_behavior

    [Fact]
    public async Task continue_happy_path()
    {
        // Should be good
        await Scenario(x =>
        {
            x.Post.Json(new NumberMessage(3)).ToUrl("/problems");
        });
    }

    [Fact]
    public async Task stop_with_problems_if_middleware_trips_off()
    {
        // This is the "sad path" that should spawn a ProblemDetails
        // object
        var result = await Scenario(x =>
        {
            x.Post.Json(new NumberMessage(10)).ToUrl("/problems");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }

    #endregion

    [Fact]
    public void adds_default_problem_details_to_open_api_metadata()
    {
        var endpoints = Host.Services.GetServices<EndpointDataSource>().SelectMany(x => x.Endpoints).OfType<RouteEndpoint>()
            .ToList();

        var endpoint = endpoints.Single(x => x.RoutePattern.RawText == "/problems");

        var produces = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Single(x => x.Type == typeof(ProblemDetails));
        produces.StatusCode.ShouldBe(400);
        produces.ContentTypes.Single().ShouldBe("application/problem+json");
    }
}