using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Tests;

public class initializing_endpoints_from_method_call
{
    [Fact]
    public void build_pattern_using_http_pattern_with_attribute()
    {
        var endpoint = EndpointChain.ChainFor<FakeEndpoint>(x => x.SayHello());
        
        endpoint.RoutePattern.RawText.ShouldBe("/hello");
        endpoint.RoutePattern.Parameters.Any().ShouldBeFalse();

    }

    [Theory]
    [InlineData(nameof(FakeEndpoint.SayHello), typeof(string))]
    [InlineData(nameof(FakeEndpoint.SayHelloAsync), typeof(string))]
    [InlineData(nameof(FakeEndpoint.SayHelloAsync2), typeof(string))]
    [InlineData(nameof(FakeEndpoint.Go), null)]
    [InlineData(nameof(FakeEndpoint.GoAsync), null)]
    [InlineData(nameof(FakeEndpoint.GoAsync2), null)]
    [InlineData(nameof(FakeEndpoint.GetResponse), typeof(BigResponse))]
    [InlineData(nameof(FakeEndpoint.GetResponseAsync), typeof(BigResponse))]
    [InlineData(nameof(FakeEndpoint.GetResponseAsync2), typeof(BigResponse))]
    public void determine_resource_type(string methodName, Type? expectedType)
    {
        var method = new MethodCall(typeof(FakeEndpoint), methodName);
        var endpoint = new EndpointChain(method);

        if (expectedType == null)
        {
            endpoint.ResourceType.ShouldBeNull();
        }
        else
        {
            endpoint.ResourceType.ShouldBe(expectedType);
        }
    }
}



public class FakeEndpoint
{
    [HttpGet("/hello")]
    public string SayHello()
    {
        return "Hello";
    }
    
    [HttpGet("/hello/async")]
    public Task<string> SayHelloAsync()
    {
        return Task.FromResult("Hello");
    }
    
    [HttpGet("/hello/async2")]
    public ValueTask<string> SayHelloAsync2()
    {
        return ValueTask.FromResult("Hello");
    }

    [HttpPost("/go")]
    public void Go()
    {
        
    }

    [HttpPost("/go/async")]
    public Task GoAsync()
    {
        return Task.CompletedTask;
    }
    
    [HttpPost("/go/async2")]
    public ValueTask GoAsync2()
    {
        return ValueTask.CompletedTask;
    }

    [HttpGet("/response")]
    public BigResponse GetResponse()
    {
        return new BigResponse();
    }
    
    [HttpGet("/response2")]
    public Task<BigResponse> GetResponseAsync()
    {
        return Task.FromResult(new BigResponse());
    }
    
        
    [HttpGet("/response3")]
    public ValueTask<BigResponse> GetResponseAsync2()
    {
        return ValueTask.FromResult(new BigResponse());
    }
}

public class BigResponse{}