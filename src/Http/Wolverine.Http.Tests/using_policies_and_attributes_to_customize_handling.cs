using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class using_policies_and_attributes_to_customize_handling : IntegrationContext
{
    [Fact]
    public void wolverine_endpoints_should_have_metadata_from_custom_endpoint_policy()
    {
        // There's a simplistic endpoint policy in the target app that adds CustomMetadata to each
        // endpoint. Just proving that the endpoint policies are hooked up
        
        var endpoints = Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(x => x.Metadata.GetMetadata<WolverineMarker>() != null).ToArray();
        
        endpoints.Any().ShouldBeTrue();

        foreach (var endpoint in endpoints)
        {
            endpoint.Metadata.GetMetadata<CustomMetadata>().ShouldNotBeNull();
        }
    }

    [Fact]
    public void attribute_usage_on_handler_level()
    {
        var endpoints = Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(x => x.Metadata.GetMetadata<WolverineMarker>() != null).ToArray();
        
        endpoints.Any().ShouldBeTrue();

        var testEndpoints = endpoints.Select(x => x.Metadata.GetMetadata<HttpChain>())
            .Where(x => x != null).Where(x => x.Method.HandlerType == typeof(TestEndpoints));

        foreach (var endpoint in testEndpoints)
        {
            endpoint.Middleware.OfType<CommentFrame>().Any().ShouldBeTrue();
        }
    }

    [Fact]
    public void attribute_usage_on_a_single_method()
    {
        var endpoints = Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(x => x.Metadata.GetMetadata<WolverineMarker>() != null).ToArray();
        
        endpoints.Any().ShouldBeTrue();

        var endpoint = endpoints.Select(x => x.Metadata.GetMetadata<HttpChain>())
            .Where(x => x != null).Single(x => x.RoutePattern.RawText == "/data/{id}");

        endpoint.Middleware.OfType<CommentFrame>().Any().ShouldBeTrue();
    }

    public using_policies_and_attributes_to_customize_handling(AppFixture fixture) : base(fixture)
    {
    }
}