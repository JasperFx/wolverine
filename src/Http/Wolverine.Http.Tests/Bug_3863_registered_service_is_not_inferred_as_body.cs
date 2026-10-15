using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Http;

namespace Wolverine.Http.Tests;

public class Bug_3863_registered_service_is_not_inferred_as_body
{
    [Fact]
    public async Task RegisteredServiceIsInjectedOnBodylessPost()
    {
        await using var host = await startHostAsync();

        var response = await host.Scenario(x =>
        {
            x.Post.Url("/gh3863/service/123");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("123:from-di");
    }

    [Fact]
    public async Task RegisteredServiceBeforeBodyDoesNotClaimBody()
    {
        await using var host = await startHostAsync();

        var response = await host.Scenario(x =>
        {
            x.Post.Json(new Gh3863Payload("from-body")).ToUrl("/gh3863/mixed");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("from-di:from-body");
    }

    [Fact]
    public async Task BodyBeforeRegisteredServiceKeepsBothBindings()
    {
        await using var host = await startHostAsync();

        var response = await host.Scenario(x =>
        {
            x.Post.Json(new Gh3863Payload("from-body")).ToUrl("/gh3863/body-first");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("from-body:from-di");
    }

    [Fact]
    public async Task ExplicitFromBodyStillWinsWhenTypeIsRegistered()
    {
        await using var host = await startHostAsync();

        var response = await host.Scenario(x =>
        {
            x.Post.Json(new Gh3863RegisteredPayload("from-body")).ToUrl("/gh3863/registered-body");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("from-body");
    }

    [Fact]
    public async Task KeyedOnlyServiceRegistrationDoesNotSuppressBodyInference()
    {
        await using var host = await startHostAsync();

        var response = await host.Scenario(x =>
        {
            x.Post.Json(new Gh3863KeyedPayload("from-body")).ToUrl("/gh3863/keyed-body");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("from-body");
    }

    [Fact]
    public async Task UnregisteredConcreteParameterStillBindsAsBody()
    {
        await using var host = await startHostAsync();

        var response = await host.Scenario(x =>
        {
            x.Post.Json(new Gh3863Payload("from-body")).ToUrl("/gh3863/body");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("from-body");
    }

    [Fact]
    public async Task GetStillResolvesSameServiceFromContainer()
    {
        await using var host = await startHostAsync();

        var response = await host.Scenario(x =>
        {
            x.Get.Url("/gh3863/service/123");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("123:from-di");
    }

    private static async Task<IAlbaHost> startHostAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Services.AddSingleton<Gh3863Resolver>();
            opts.Services.AddSingleton(new Gh3863RegisteredPayload("from-di"));
            opts.Services.AddKeyedSingleton<Gh3863KeyedPayload>("service-key", new Gh3863KeyedPayload("from-di"));
            opts.Durability.Mode = DurabilityMode.MediatorOnly;
            opts.ApplicationAssembly = typeof(Bug_3863_registered_service_is_not_inferred_as_body).Assembly;
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh3863Endpoints));
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the GH-3863 endpoints", type => type != typeof(Gh3863Endpoints)))));
    }
}

public class Gh3863Resolver
{
    public string Value => "from-di";
}

public record Gh3863Payload(string Value);

public record Gh3863RegisteredPayload(string Value);

public record Gh3863KeyedPayload(string Value);

public static class Gh3863Endpoints
{
    [WolverinePost("/gh3863/service/{id}")]
    public static string Post(string id, Gh3863Resolver resolver) => $"{id}:{resolver.Value}";

    [WolverineGet("/gh3863/service/{id}")]
    public static string Get(string id, Gh3863Resolver resolver) => $"{id}:{resolver.Value}";

    [WolverinePost("/gh3863/mixed")]
    public static string Mixed(Gh3863Resolver resolver, Gh3863Payload payload) => $"{resolver.Value}:{payload.Value}";

    [WolverinePost("/gh3863/body-first")]
    public static string BodyFirst(Gh3863Payload payload, Gh3863Resolver resolver) => $"{payload.Value}:{resolver.Value}";

    [WolverinePost("/gh3863/registered-body")]
    public static string RegisteredBody([FromBody] Gh3863RegisteredPayload payload) => payload.Value;

    [WolverinePost("/gh3863/keyed-body")]
    public static string KeyedBody(Gh3863KeyedPayload payload) => payload.Value;

    [WolverinePost("/gh3863/body")]
    public static string Body(Gh3863Payload payload) => payload.Value;
}
