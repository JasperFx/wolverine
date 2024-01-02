using System.Diagnostics;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
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

    [Fact]
    public void derive_the_operation_id()
    {
        var (_, op) = FindOpenApiDocument(OperationType.Get, "/result");
        
        op.OperationId.ShouldBe("WolverineWebApi.ResultEndpoints.GetResult");
    }

    [Fact]
    public void apply_tags_from_tags_attribute()
    {
        var endpoint = EndpointFor("/users/sign-up");
        var tags = endpoint.Metadata.GetOrderedMetadata<ITagsMetadata>();
        tags.Any().ShouldBeTrue();
        
        var (item, op) = FindOpenApiDocument(OperationType.Post, "/users/sign-up");
        op.Tags.ShouldContain(x => x.Name == "Users");
    }
    
    
    
    

}