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
}