using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Swashbuckle.AspNetCore.Swagger;

namespace Wolverine.Http.Tests;

public class swashbuckle_integration : IntegrationContext
{
    public swashbuckle_integration(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task wolverine_stuff_is_in_the_document()
    {
        var results = await Scenario(x => { x.Get.Url("/swagger/v1/swagger.json"); });

        var doc = results.ReadAsText();

        doc.ShouldContain("/fromservice");
        
        doc.ShouldNotContain("/ignore");
    }

    [Fact]
    public void ignore_endpoint_methods_that_are_marked_with_ExcludeFromDescription()
    {
        HttpChains.Chains.Any(x => x.RoutePattern.RawText == "/ignore").ShouldBeTrue();
        
        var generator = Host.Services.GetRequiredService<ISwaggerProvider>();
        var doc = generator.GetSwagger("v1");
        
        doc.Paths.Any(x => x.Key == "/ignore").ShouldBeFalse();
    }
    
    

}