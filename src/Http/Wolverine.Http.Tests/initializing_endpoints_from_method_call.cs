using System.Text.Json;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Runtime;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class initializing_endpoints_from_method_call : IntegrationContext
{
    private readonly IServiceContainer container;
    private readonly HttpGraph parent;

    public initializing_endpoints_from_method_call(AppFixture fixture) : base(fixture)
    {
        var registry = new ServiceCollection();
        registry.AddSingleton<JsonSerializerOptions>();
        registry.AddTransient<IServiceVariableSource>(c => new ServiceCollectionServerVariableSource((ServiceContainer)c));
        
        container = new ServiceContainer(registry, registry.BuildServiceProvider());

        parent = new HttpGraph(new WolverineOptions
        {
            ApplicationAssembly = GetType().Assembly
        }, container);
    }

    [Fact]
    public void build_pattern_using_http_pattern_with_attribute()
    {
        var endpoint = HttpChain.ChainFor<FakeEndpoint>(x => x.SayHello());

        endpoint.RoutePattern.RawText.ShouldBe("/fake/hello");
        endpoint.RoutePattern.Parameters.Any().ShouldBeFalse();
    }

    [Fact]
    public void default_operation_id_is_endpoint_class_and_method()
    {
        var endpoint = HttpChain.ChainFor<FakeEndpoint>(x => x.SayHello());
        endpoint.OperationId.ShouldBe($"{typeof(FakeEndpoint).FullNameInCode()}.{nameof(FakeEndpoint.SayHello)}");
    }

    [Fact]
    public void override_operation_id_on_attributes()
    {
        var endpoint = HttpChain.ChainFor<FakeEndpoint>(x => x.SayHelloAsync());
        endpoint.OperationId.ShouldBe("OverriddenId");
    }

    [Fact]
    public void capturing_the_http_method_metadata()
    {
        var chain = HttpChain.ChainFor<FakeEndpoint>(x => x.SayHello());
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        var metadata = endpoint.Metadata.OfType<HttpMethodMetadata>().Single();
        metadata.HttpMethods.Single().ShouldBe("GET");
    }

    [Fact]
    public void capturing_accepts_metadata_for_request_type()
    {
        var chain = HttpChain.ChainFor(typeof(TestEndpoints), nameof(TestEndpoints.PostJson));
        chain.RequestType.ShouldBe(typeof(Question));

        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        var metadata = endpoint.Metadata.OfType<IAcceptsMetadata>()
            .Single();

        metadata.RequestType.ShouldBe(chain.RequestType);
        metadata.ContentTypes.Single().ShouldBe("application/json");
    }

    [Fact]
    public void capturing_metadata_for_resource_type()
    {
        var chain = HttpChain.ChainFor(typeof(TestEndpoints), nameof(TestEndpoints.PostJson));
        chain.ResourceType.ShouldBe(typeof(ArithmeticResults));

        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
        var metadata = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().ToArray();
        metadata.Length.ShouldBeGreaterThanOrEqualTo(2);

        var responseBody = metadata.FirstOrDefault(x => x.StatusCode == 200);
        responseBody.Type.ShouldBe(typeof(ArithmeticResults));
        responseBody.ContentTypes.Single().ShouldBe("application/json");

        var noValue = metadata.FirstOrDefault(x => x.StatusCode == 404);
        noValue.ContentTypes.Any().ShouldBeFalse();
        noValue.Type.ShouldBe(typeof(void));
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
        var endpoint = new HttpChain(method, parent);

        if (expectedType == null)
        {
            endpoint.ResourceType.ShouldBe(typeof(void));
            endpoint.NoContent.ShouldBeTrue();
        }
        else
        {
            endpoint.ResourceType.ShouldBe(expectedType);
        }
    }

    [Fact]
    public void pick_up_metadata_from_attribute_on_handler_type()
    {
        var chain = HttpChain.ChainFor<SecuredEndpoint>(x => x.Greetings());
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        endpoint.Metadata.OfType<AuthorizeAttribute>().ShouldNotBeNull();
    }

    [Fact]
    public void pick_up_metadata_from_attribute_on_method()
    {
        var chain = HttpChain.ChainFor<IndividualEndpoint>(x => x.Goodbypes());
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        endpoint.Metadata.OfType<AuthorizeAttribute>().ShouldNotBeNull();
    }

    [Fact]
    public void must_use_outbox_when_using_message_bus()
    {
        var chain = HttpChain.ChainFor<MaybeMessagingEndpoints>(x => x.Yes(null, null));
        chain.RequiresOutbox().ShouldBeTrue();
    }

    [Fact]
    public void does_not_use_outbox_when_not_using_message_bus()
    {
        var chain = HttpChain.ChainFor<MaybeMessagingEndpoints>(x => x.No(null));
        chain.RequiresOutbox().ShouldBeFalse();
    }

    [Fact]
    public void default_tenancy_is_null()
    {
        var chain = HttpChain.ChainFor<MaybeMessagingEndpoints>(x => x.No(null));
        chain.TenancyMode.ShouldBeNull();
    }

    [Fact]
    public void add_from_route_metadata()
    {
        var chain = HttpChain.ChainFor<RoutedEndpoint>(x => x.Get(null));
    }
}

public class RoutedEndpoint
{
    [WolverineGet("/routed/{name}")]
    public string Get(string name)
    {
        return name;
    }
}

public class MaybeMessagingEndpoints
{
    [WolverinePost("/messaging/yes")]
    public Task Yes(Question question, IMessageBus bus)
    {
        return Task.CompletedTask;
    }

    [WolverinePost("/messaging/no")]
    public Task No(Question question)
    {
        return Task.CompletedTask;
    }
}

[Authorize]
public class SecuredEndpoint
{
    [WolverineGet("/greetings")]
    public string Greetings()
    {
        return "Salutations!";
    }
}

public class IndividualEndpoint
{
    [WolverineGet("/goodbyes")]
    [Authorize]
    public string Goodbypes()
    {
        return "Until later";
    }
}