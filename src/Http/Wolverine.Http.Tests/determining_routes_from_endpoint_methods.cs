using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Tests;

public class determining_routes_from_endpoint_methods
{
    [Fact]
    public void build_using_http_pattern_simple()
    {
        var endpoint = EndpointChain.ChainFor<FakeEndpoint>(x => x.SayHello());
        
        endpoint.RoutePattern.RawText.ShouldBe("/hello/there");
    }
}



public class FakeEndpoint
{
    [HttpGet("/hello/there/{name}")]
    public string SayHello()
    {
        return "Hello";
    }
}