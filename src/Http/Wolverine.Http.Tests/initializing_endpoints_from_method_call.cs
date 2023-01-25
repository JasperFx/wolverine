using System.Text.Json;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Lamar;
using Shouldly;
using TestEndpoints;

namespace Wolverine.Http.Tests;

public class initializing_endpoints_from_method_call : IDisposable
{
    private readonly Container container;
    private readonly EndpointGraph parent;

    public initializing_endpoints_from_method_call()
    {
        container = new Container(x =>
        {
            x.ForConcreteType<JsonSerializerOptions>().Configure.Singleton();
            x.For<IServiceVariableSource>().Use(c => c.CreateServiceVariableSource()).Singleton();
        });

        parent = new EndpointGraph(new WolverineOptions { ApplicationAssembly = GetType().Assembly }, container);
    }

    public void Dispose()
    {
        container.Dispose();
    }

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


        var endpoint = new EndpointChain(method, parent);

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

