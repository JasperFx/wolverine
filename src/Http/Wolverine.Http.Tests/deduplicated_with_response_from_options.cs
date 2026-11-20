using System.Collections.Concurrent;
using Alba;
using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Http.Policies;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-5617. [DeduplicatedWithResponse] applied across endpoints from WolverineHttpOptions.DeduplicateWithResponse().
/// </summary>
public class deduplicated_with_response_from_options : IAsyncLifetime
{
    private const string SchemaName = "http_dedup_from_options";

    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
            opts.Durability.EnableDeduplicatedResponses = true;

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(deduplicated_with_response_from_options).Assembly);
        });

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a DeduplicateWithResponse() test endpoint",
                    type => type != typeof(DeduplicatedFromOptionsEndpoints) &&
                            type != typeof(OptedOutOfDeduplicationEndpoints)));

            #region sample_deduplicate_with_response_on_every_post

            // Every POST endpoint that returns a resource is deduplicated on its
            // Idempotency-Key header, as if it had
            // [DeduplicatedWithResponse(DeduplicationScope.User | DeduplicationScope.Endpoint)]
            opts.DeduplicateWithResponse(DeduplicationScope.User | DeduplicationScope.Endpoint);

            #endregion

            #region sample_deduplicate_with_response_with_a_filter

            // The PUT endpoints too, with a key that is optional and a one hour window
            opts.DeduplicateWithResponse(DeduplicationScope.User,
                chain => chain.HttpMethods.Contains("PUT"),
                x =>
                {
                    x.Required = false;
                    x.Window = 1.Hours();
                });

            #endregion
        }));

        await ((IHost)theHost).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public void an_endpoint_without_the_attribute_gets_it()
    {
        var requirement = chainFor("/dedup-options/orders").DeduplicatedWithResponse.ShouldNotBeNull();

        requirement.Scope.ShouldBe(DeduplicationScope.User | DeduplicationScope.Endpoint);
        requirement.Source.ShouldBe(ValueSource.Anything);
        requirement.KeyName.ShouldBe("Idempotency-Key");
        requirement.Required.ShouldBeTrue();
        requirement.Window.ShouldBeNull();
        requirement.StoreBody.ShouldBeTrue();

        var codes = statusCodesFor("/dedup-options/orders");
        codes.ShouldContain(400);
        codes.ShouldContain(409);
        codes.ShouldContain(422);
    }

    [Fact]
    public void an_endpoint_with_its_own_attribute_keeps_its_settings()
    {
        // [DeduplicatedWithResponse(DeduplicationScope.User, Key = "X-Request-Id", WindowInSeconds = 60)] on a PUT
        var requirement = chainFor("/dedup-options/own/{id}", "PUT").DeduplicatedWithResponse.ShouldNotBeNull();

        requirement.Scope.ShouldBe(DeduplicationScope.User | DeduplicationScope.Endpoint);
        requirement.KeyName.ShouldBe("X-Request-Id");
        requirement.Window.ShouldBe(60.Seconds());

        // Left unset on the attribute, so it comes from the PUT call
        requirement.Required.ShouldBeFalse();
    }

    [Fact]
    public void an_endpoint_that_sets_its_scope_keeps_it()
    {
        // [DeduplicatedWithResponse(DeduplicationScope.User, StoreBody = false)] on a POST
        var requirement = chainFor("/dedup-options/secret").DeduplicatedWithResponse.ShouldNotBeNull();

        requirement.Scope.ShouldBe(DeduplicationScope.User);
        requirement.StoreBody.ShouldBeFalse();
        requirement.Required.ShouldBeTrue();
    }

    [Fact]
    public async Task an_endpoint_that_returns_no_resource_is_skipped()
    {
        chainFor("/dedup-options/no-resource").DeduplicatedWithResponse.ShouldBeNull();
        statusCodesFor("/dedup-options/no-resource").ShouldNotContain(409);

        // No key needed
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DeduplicatedOrder("nothing")).ToUrl("/dedup-options/no-resource");
            x.StatusCodeShouldBe(204);
        });
    }

    [Fact]
    public async Task an_endpoint_whose_response_is_not_json_is_skipped()
    {
        chainFor("/dedup-options/text").DeduplicatedWithResponse.ShouldBeNull();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new DeduplicatedOrder("text")).ToUrl("/dedup-options/text");
            x.ContentShouldBe("text");
        });
    }

    [Fact]
    public void endpoints_no_call_matches_are_untouched()
    {
        chainFor("/dedup-options/orders/{id}", "GET").DeduplicatedWithResponse.ShouldBeNull();
        chainFor("/dedup-options/orders/{id}", "PATCH").DeduplicatedWithResponse.ShouldBeNull();

        statusCodesFor("/dedup-options/orders/{id}", "PATCH").ShouldNotContain(409);
    }

    [Fact]
    public void a_filter_covers_other_methods()
    {
        var requirement = chainFor("/dedup-options/orders/{id}", "PUT").DeduplicatedWithResponse.ShouldNotBeNull();

        requirement.Scope.ShouldBe(DeduplicationScope.User);
        requirement.Required.ShouldBeFalse();
        requirement.Window.ShouldBe(1.Hours());
    }

    [Fact]
    public void an_endpoint_can_opt_out()
    {
        chainFor("/dedup-options/opted-out").DeduplicatedWithResponse.ShouldBeNull();
        chainFor("/dedup-options/opted-out-class").DeduplicatedWithResponse.ShouldBeNull();

        statusCodesFor("/dedup-options/opted-out").ShouldNotContain(409);
        statusCodesFor("/dedup-options/opted-out-class").ShouldNotContain(409);
    }

    [Fact]
    public async Task a_repeat_is_answered_with_the_first_response()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("replayed-" + key);

        var first = await postAsync(request, key, 201);
        var repeat = await postAsync(request, key, 201);

        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());
        repeat.Context.Response.Headers.Location.ToString()
            .ShouldBe(first.Context.Response.Headers.Location.ToString());
        repeat.Context.Response.Headers["Idempotent-Replayed"].ToString().ShouldBe("true");

        DeduplicatedFromOptionsEndpoints.Runs(request.Name).ShouldBe(1);

        // A different request with the same key
        await postAsync(new DeduplicatedOrder("other-" + key), key, 422);
    }

    [Fact]
    public async Task a_missing_key_is_refused()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DeduplicatedOrder("no-key")).ToUrl("/dedup-options/orders");
            x.StatusCodeShouldBe(400);
        });

        DeduplicatedFromOptionsEndpoints.Runs("no-key").ShouldBe(0);
    }

    private Task<IScenarioResult> postAsync(DeduplicatedOrder body, string key, int status)
        => theHost.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/dedup-options/orders");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(status);
        });

    private HttpChain chainFor(string route, string method = "POST")
        => theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == route && x.HttpMethods.Contains(method));

    private int[] statusCodesFor(string route, string method = "POST")
        => theHost.Services.GetServices<EndpointDataSource>().SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => x.RoutePattern.RawText == route &&
                         x.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method))
            .Metadata.OfType<IProducesResponseTypeMetadata>().Select(x => x.StatusCode).ToArray();
}

/// <summary>
/// GH-5617. <c>[DeduplicatedWithResponse]</c> with no scope takes it, and anything else it leaves unset, from
/// <c>DeduplicateWithResponse()</c>. Built directly, because a host without that call refuses the endpoint.
/// </summary>
public class deduplicated_with_response_inheriting_from_options
{
    [Fact]
    public void the_attribute_with_no_scope_takes_the_rest_from_the_options()
    {
        var options = new WolverineHttpOptions();
        options.DeduplicateWithResponse(DeduplicationScope.User | DeduplicationScope.Tenant,
            configure: x => x.Window = 30.Minutes());

        var chain = HttpChain.ChainFor(typeof(InheritingDeduplicatedResponse), nameof(InheritingDeduplicatedResponse.Post));
        applyPolicies(options, chain);

        var requirement = chain.DeduplicatedWithResponse.ShouldNotBeNull();
        requirement.Scope.ShouldBe(DeduplicationScope.User | DeduplicationScope.Tenant);
        requirement.Window.ShouldBe(30.Minutes());
        requirement.StoreBody.ShouldBeFalse();

        Should.NotThrow(() => chain.FinalizeDeduplicatedWithResponse());
    }

    [Fact]
    public void the_attribute_with_no_scope_is_refused_when_no_call_covers_it()
    {
        // Covers PUT only; the endpoint is a POST
        var options = new WolverineHttpOptions();
        options.DeduplicateWithResponse(DeduplicationScope.User, chain => chain.HttpMethods.Contains("PUT"));

        var chain = HttpChain.ChainFor(typeof(InheritingDeduplicatedResponse), nameof(InheritingDeduplicatedResponse.Post));
        applyPolicies(options, chain);

        Should.Throw<InvalidOperationException>(() => chain.FinalizeDeduplicatedWithResponse())
            .Message.ShouldContain("no WolverineHttpOptions.DeduplicateWithResponse() call covers this endpoint");
    }

    [Fact]
    public void the_key_is_one_setting()
    {
        var options = new WolverineHttpOptions();
        options.DeduplicateWithResponse(DeduplicationScope.User, configure: x =>
        {
            x.Source = ValueSource.FromQueryString;
            x.Key = "requestId";
        });

        var chain = HttpChain.ChainFor(typeof(InheritingDeduplicatedResponse),
            nameof(InheritingDeduplicatedResponse.PostWithItsOwnHeader));
        applyPolicies(options, chain);

        // The endpoint named a header, so it keeps the header source too
        var requirement = chain.DeduplicatedWithResponse.ShouldNotBeNull();
        requirement.Source.ShouldBe(ValueSource.Anything);
        requirement.Key.ShouldBe("X-Request-Id");
        requirement.Scope.ShouldBe(DeduplicationScope.User);
    }

    [Fact]
    public void the_first_call_that_matches_wins()
    {
        var options = new WolverineHttpOptions();
        options.DeduplicateWithResponse(DeduplicationScope.User, configure: x => x.Required = false);
        options.DeduplicateWithResponse(DeduplicationScope.Tenant | DeduplicationScope.User,
            configure: x => x.Window = 5.Minutes());

        var chain = HttpChain.ChainFor(typeof(InheritingDeduplicatedResponse), nameof(InheritingDeduplicatedResponse.Post));
        applyPolicies(options, chain);

        var requirement = chain.DeduplicatedWithResponse.ShouldNotBeNull();
        requirement.Scope.ShouldBe(DeduplicationScope.User);
        requirement.Required.ShouldBeFalse();

        // The first call left the window unset
        requirement.Window.ShouldBe(5.Minutes());
    }

    [Fact]
    public void no_scope_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new WolverineHttpOptions().DeduplicateWithResponse(DeduplicationScope.None));
    }

    [Fact]
    public void a_window_that_is_not_positive_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new WolverineHttpOptions().DeduplicateWithResponse(DeduplicationScope.User,
                configure: x => x.Window = TimeSpan.Zero));
    }

    private static void applyPolicies(WolverineHttpOptions options, HttpChain chain)
    {
        foreach (var policy in options.Policies.OfType<DeduplicateWithResponsePolicy>())
        {
            policy.Apply([chain], new GenerationRules(), null!);
        }
    }
}

public static class DeduplicatedFromOptionsEndpoints
{
    private static readonly ConcurrentDictionary<string, int> _runs = new();

    public static int Runs(string name) => _runs.GetValueOrDefault(name);

    // No attribute: covered by DeduplicateWithResponse()
    [WolverinePost("/dedup-options/orders")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request)
    {
        _runs.AddOrUpdate(request.Name, 1, (_, count) => count + 1);
        return new DeduplicatedOrderCreated(Guid.NewGuid());
    }

    // Keeps its own scope, and takes everything else from DeduplicateWithResponse()
    [DeduplicatedWithResponse(DeduplicationScope.User, StoreBody = false)]
    [WolverinePost("/dedup-options/secret")]
    public static DeduplicatedOrderCreated PostSecret(DeduplicatedOrder request) => new(Guid.NewGuid());

    [DeduplicatedWithResponse(DeduplicationScope.User | DeduplicationScope.Endpoint, Key = "X-Request-Id",
        WindowInSeconds = 60)]
    [WolverinePut("/dedup-options/own/{id}")]
    public static DeduplicatedOrderCreated PutOwn(Guid id, DeduplicatedOrder request) => new(id);

    // Returns no resource, so it is skipped rather than refused
    [WolverinePost("/dedup-options/no-resource")]
    public static void PostNothing(DeduplicatedOrder request)
    {
    }

    // Written as text, not JSON, so it is skipped too
    [WolverinePost("/dedup-options/text")]
    public static string PostText(DeduplicatedOrder request) => request.Name;

    [WolverineGet("/dedup-options/orders/{id}")]
    public static DeduplicatedOrderCreated Get(Guid id) => new(id);

    [WolverinePut("/dedup-options/orders/{id}")]
    public static DeduplicatedOrderCreated Put(Guid id, DeduplicatedOrder request) => new(id);

    [WolverinePatch("/dedup-options/orders/{id}")]
    public static DeduplicatedOrderCreated Patch(Guid id, DeduplicatedOrder request) => new(id);

    #region sample_disable_deduplicated_with_response

    // Left out of DeduplicateWithResponse()
    [DisableDeduplicatedWithResponse]
    [WolverinePost("/dedup-options/opted-out")]
    public static DeduplicatedOrderCreated PostOptedOut(DeduplicatedOrder request) => new(Guid.NewGuid());

    #endregion
}

[DisableDeduplicatedWithResponse]
public static class OptedOutOfDeduplicationEndpoints
{
    [WolverinePost("/dedup-options/opted-out-class")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
}

// No scope of its own, so a host without DeduplicateWithResponse() refuses it. Hidden from discovery for that
// reason; its tests build the chain directly.
[WolverineIgnore]
public static class InheritingDeduplicatedResponse
{
    #region sample_deduplicated_with_response_inheriting_the_scope

    // The scope, key and window come from DeduplicateWithResponse()
    [DeduplicatedWithResponse(StoreBody = false)]
    [WolverinePost("/dedup-options/inheriting")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());

    #endregion

    [DeduplicatedWithResponse(Key = "X-Request-Id")]
    [WolverinePost("/dedup-options/inheriting-header")]
    public static DeduplicatedOrderCreated PostWithItsOwnHeader(DeduplicatedOrder request) => new(Guid.NewGuid());
}
