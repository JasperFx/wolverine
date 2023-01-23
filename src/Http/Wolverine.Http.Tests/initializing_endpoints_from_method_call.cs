using TestEndpoints;
using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Builder;
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
        var endpoint = new EndpointChain(method, new EndpointGraph());

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

public class BigResponse{}