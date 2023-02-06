using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Http.Tests;

public class endpoint_metadata : IntegrationContext
{
    public endpoint_metadata(AppFixture fixture) : base(fixture)
    {
        var sources = fixture.Host.Services.GetServices<EndpointDataSource>();
        AllEndpoints = sources.SelectMany(x => x.Endpoints).ToArray();
        
    }

    public Endpoint[] AllEndpoints { get; }

    [Fact]
    public void metadata_for_request_types()
    {
        
    }
     
    [Fact]
    public void check_it_out()
    {
        foreach (Endpoint endpoint in AllEndpoints)
        {
            Debug.WriteLine(endpoint.DisplayName);
        }
    }
}