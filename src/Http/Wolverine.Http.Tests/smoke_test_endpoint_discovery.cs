using System.Text.Json;
using JasperFx.CodeGeneration.Model;
using Lamar;
using Shouldly;

namespace Wolverine.Http.Tests;

public class smoke_test_endpoint_discovery
{
    [Fact]
    public async Task discover_and_build_endpoints()
    {
        var container = new Container(x =>
        {
            x.For<JsonSerializerOptions>().Use(new JsonSerializerOptions());
            x.For<IServiceVariableSource>().Use(c => c.CreateServiceVariableSource()).Singleton();
        });
        
        var parent = new EndpointGraph(new WolverineOptions{ApplicationAssembly = GetType().Assembly}, container);

        await parent.DiscoverEndpoints();
        
        parent.Endpoints.Any().ShouldBeTrue();
    }
}