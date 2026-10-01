using System.Security.Claims;
using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Http.Runtime;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-4742. [DeduplicatedWithResponse] on the PostgreSQL message store, with no persistence provider. Only
/// EnableDeduplicatedResponses is on: the feature needs nothing from [Deduplicated].
/// </summary>
public class deduplicated_with_response : IAsyncLifetime
{
    private const string SchemaName = "http_deduplicated_response";

    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
            opts.Durability.EnableDeduplicatedResponses = true;
            opts.Durability.DeduplicationWindow = 1.Hours();

            opts.Discovery.DisableConventionalDiscovery();

            // The application assembly is cached process-wide; include this one explicitly.
            opts.Discovery.IncludeAssembly(typeof(deduplicated_with_response).Assembly);
        });

        builder.Services.AddWolverineHttp();

        // No tenant id detection, deliberately -- see a_tenant_scope_needs_tenant_detection.
        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a [DeduplicatedWithResponse] test endpoint",
                    type => type != typeof(DeduplicatedResponseEndpoints) && type != typeof(EarlyExitDeduplicatedEndpoint)
                            && type != typeof(MissingResourceDeduplicatedEndpoint)))));

        await ((IHost)theHost).ResetResourceState();

        DeduplicatedResponseEndpoints.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task a_repeat_of_the_same_request_is_answered_with_the_first_response()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("first");

        var first = await postAsync(request, key, 201);
        var repeat = await postAsync(request, key, 201);

        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());
        repeat.Context.Response.Headers.Location.ToString()
            .ShouldBe(first.Context.Response.Headers.Location.ToString());

        DeduplicatedResponseEndpoints.Runs("first").ShouldBe(1);
    }

    [Fact]
    public async Task a_different_body_under_the_same_key_is_a_422()
    {
        var key = Guid.NewGuid().ToString();

        await postAsync(new DeduplicatedOrder("original"), key, 201);
        await postAsync(new DeduplicatedOrder("changed"), key, 422);

        DeduplicatedResponseEndpoints.Runs("changed").ShouldBe(0);
    }

    [Fact]
    public async Task a_different_query_string_under_the_same_key_is_a_422()
    {
        var key = Guid.NewGuid().ToString();

        await postAsync(new DeduplicatedOrder("query"), key, 201, query: "a=1");
        await postAsync(new DeduplicatedOrder("query"), key, 422, query: "a=2");
    }

    [Fact]
    public async Task the_same_key_and_body_on_another_endpoint_is_a_422()
    {
        // The scope leaves the endpoint out, so the claim is shared; the fingerprint still tells them apart.
        var key = Guid.NewGuid().ToString();

        await postAsync(new DeduplicatedOrder("elsewhere"), key, 201);
        await postAsync(new DeduplicatedOrder("elsewhere"), key, 422, url: "/deduplicated-response/elsewhere");
    }

    [Fact]
    public async Task a_missing_key_is_a_400()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DeduplicatedOrder("keyless")).ToUrl("/deduplicated-response/orders");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task a_throw_releases_the_claim_and_the_retry_does_the_work()
    {
        var key = Guid.NewGuid().ToString();

        await Should.ThrowAsync<InvalidOperationException>(() => theHost.Scenario(x =>
        {
            x.Post.Json(new DeduplicatedOrder(DeduplicatedResponseEndpoints.Throw))
                .ToUrl("/deduplicated-response/orders");
            x.WithRequestHeader("Idempotency-Key", key);
        }));

        await postAsync(new DeduplicatedOrder("after the throw"), key, 201);
    }

    [Fact]
    public async Task a_400_and_up_answer_releases_the_claim_and_the_retry_does_the_work()
    {
        var key = Guid.NewGuid().ToString();

        await postAsync(new DeduplicatedOrder(DeduplicatedResponseEndpoints.Missing), key, 404);
        await postAsync(new DeduplicatedOrder("after the 404"), key, 201);
    }

    [Fact]
    public async Task a_repeat_while_the_first_request_is_still_running_is_a_409_then_the_replay()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder(DeduplicatedResponseEndpoints.Slow);

        var first = postAsync(request, key, 201);
        await DeduplicatedResponseEndpoints.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        try
        {
            await postAsync(request, key, 409);
        }
        finally
        {
            DeduplicatedResponseEndpoints.ReleaseSlowRequest();
        }

        var winner = await first;

        var repeat = await postAsync(request, key, 201);
        (await repeat.ReadAsTextAsync()).ShouldBe(await winner.ReadAsTextAsync());
        DeduplicatedResponseEndpoints.Runs(DeduplicatedResponseEndpoints.Slow).ShouldBe(1);
    }

    [Fact]
    public async Task a_different_request_while_the_first_is_still_running_is_a_422()
    {
        // The fingerprint is stored with the claim, before the answer.
        var key = Guid.NewGuid().ToString();

        var first = postAsync(new DeduplicatedOrder(DeduplicatedResponseEndpoints.Slow), key, 201);
        await DeduplicatedResponseEndpoints.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        try
        {
            await postAsync(new DeduplicatedOrder("something else"), key, 422);
        }
        finally
        {
            DeduplicatedResponseEndpoints.ReleaseSlowRequest();
        }

        await first;
    }

    [Fact]
    public async Task a_user_scope_isolates_users()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("per-user");

        var han = await postAsync(request, key, 201, user: "han");
        var leia = await postAsync(request, key, 201, user: "leia");
        var hanAgain = await postAsync(request, key, 201, user: "han");

        (await leia.ReadAsTextAsync()).ShouldNotBe(await han.ReadAsTextAsync());
        (await hanAgain.ReadAsTextAsync()).ShouldBe(await han.ReadAsTextAsync());
        DeduplicatedResponseEndpoints.Runs("per-user").ShouldBe(2);
    }

    [Fact]
    public async Task an_endpoint_scope_isolates_paths()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("per-path");

        await postAsync(request, key, 201, url: "/deduplicated-response/endpoint/one");
        await postAsync(request, key, 201, url: "/deduplicated-response/endpoint/two");
        await postAsync(request, key, 201, url: "/deduplicated-response/endpoint/one");

        DeduplicatedResponseEndpoints.Runs("per-path").ShouldBe(2);
    }

    [Fact]
    public async Task a_window_sets_the_claim_expiry()
    {
        var key = Guid.NewGuid().ToString();

        var before = DateTimeOffset.UtcNow;
        await postAsync(new DeduplicatedOrder("windowed"), key, 201, url: "/deduplicated-response/windowed");

        // Anonymous, User-scoped, and stored hashed.
        var expires = await scalarAsync<DateTimeOffset>(
            $"select expires from {SchemaName}.wolverine_deduplicated_responses where deduplication_id = @id",
            DeduplicatedResponses.StorageIdFor("0:|0:|0:|" + key));
        expires.ShouldBeInRange(before.AddSeconds(55), DateTimeOffset.UtcNow.AddSeconds(65));
    }

    [Fact]
    public async Task a_tenant_scope_needs_tenant_detection()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            postAsync(new DeduplicatedOrder("tenant"), Guid.NewGuid().ToString(), 201,
                url: "/deduplicated-response/tenant"));

        ex.Message.ShouldContain("tenant id detection");
    }

    [Fact]
    public async Task the_plain_deduplication_table_is_not_provisioned()
    {
        (await scalarAsync<long>(
                "select count(*) from information_schema.tables where table_schema = @id and table_name = 'wolverine_deduplication'",
                SchemaName))
            .ShouldBe(0);
    }

    [Fact]
    public async Task an_optional_key_left_out_runs_every_time()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DeduplicatedOrder("optional")).ToUrl("/deduplicated-response/optional");
            x.StatusCodeShouldBe(201);
        });
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DeduplicatedOrder("optional")).ToUrl("/deduplicated-response/optional");
            x.StatusCodeShouldBe(201);
        });

        DeduplicatedResponseEndpoints.Runs("optional").ShouldBe(2);
    }

    [Fact]
    public async Task the_key_can_come_from_another_header()
    {
        var key = Guid.NewGuid().ToString();

        async Task<IScenarioResult> post() => await theHost.Scenario(x =>
        {
            x.Post.Json(new DeduplicatedOrder("other-header")).ToUrl("/deduplicated-response/other-header");
            x.WithRequestHeader("X-Request-Key", key);
            x.StatusCodeShouldBe(201);
        });

        var first = await post();
        var repeat = await post();

        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());
        DeduplicatedResponseEndpoints.Runs("other-header").ShouldBe(1);
    }

    [Fact]
    public async Task a_successful_early_exit_from_middleware_releases_the_claim()
    {
        // Middleware answered 202 before the endpoint ran: nothing was done, so nothing may be held.
        var key = Guid.NewGuid().ToString();

        await postAsync(new DeduplicatedOrder(EarlyExitDeduplicatedEndpoint.Early), key, 202,
            url: "/deduplicated-response/early-exit");
        await postAsync(new DeduplicatedOrder("after the early exit"), key, 201,
            url: "/deduplicated-response/early-exit");
    }

    [Fact]
    public async Task a_missing_resource_answered_with_204_is_replayed()
    {
        var key = Guid.NewGuid().ToString();

        async Task<IScenarioResult> get() => await theHost.Scenario(x =>
        {
            x.Get.Url("/deduplicated-response/maybe");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(204);
        });

        await get();
        await get();

        DeduplicatedResponseEndpoints.Runs("maybe").ShouldBe(1);
    }

    [Fact]
    public async Task a_response_that_is_not_system_text_json_is_refused()
    {
        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            postAsync(new DeduplicatedOrder("text"), Guid.NewGuid().ToString(), 200,
                url: "/deduplicated-response/text"));

        ex.Message.ShouldContain("System.Text.Json");
    }

    [Fact]
    public async Task the_response_is_recorded_before_it_is_written_and_before_messages_flush()
    {
        // Warm the route with a real request, then read the source it was built from.
        await postAsync(new DeduplicatedOrder("warm"), Guid.NewGuid().ToString(), 201);

        var graph = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var source = graph.Chains.Single(x => x.RoutePattern!.RawText == "/deduplicated-response/orders")
            .SourceCode.ShouldNotBeNull();

        var claim = source.IndexOf(".TryClaimAsync(", StringComparison.Ordinal);
        var handler = source.IndexOf(
            $"{nameof(DeduplicatedResponseEndpoints)}.{nameof(DeduplicatedResponseEndpoints.PostOrder)}(",
            StringComparison.Ordinal);
        var record = source.IndexOf(".RecordResponseAsync(", StringComparison.Ordinal);
        var write = source.IndexOf("WriteJsonAsync", StringComparison.Ordinal);
        var flush = source.IndexOf("FlushOutgoingMessagesAsync", StringComparison.Ordinal);

        claim.ShouldBeGreaterThan(-1, source);
        claim.ShouldBeLessThan(handler, source);
        handler.ShouldBeLessThan(record, source);
        record.ShouldBeLessThan(write, source);
        record.ShouldBeLessThan(flush, source);

        // Released only while unanswered, before a failure response and in the finally.
        source.ShouldContain(nameof(HttpHandler.ReleaseDeduplicatedResponseBeforeFailureResponse));
        source.ShouldContain(".ReleaseUnansweredAsync(");

        // Nothing from [Deduplicated].
        source.ShouldNotContain("IMessageDeduplicator");
    }

    private Task<IScenarioResult> postAsync(DeduplicatedOrder body, string key, int status,
        string url = "/deduplicated-response/orders", string? query = null, string? user = null)
        => theHost.Scenario(x =>
        {
            var send = x.Post.Json(body).ToUrl(url);
            if (query != null) send.QueryString(query.Split('=')[0], query.Split('=')[1]);

            x.WithRequestHeader("Idempotency-Key", key);

            if (user != null)
            {
                x.ConfigureHttpContext(c =>
                    c.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "TestAuth")));
            }

            x.StatusCodeShouldBe(status);
        });

    private static async Task<T> scalarAsync<T>(string sql, string id)
    {
        var token = TestContext.Current.CancellationToken;

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(token);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(token);
        (await reader.ReadAsync(token)).ShouldBeTrue();

        return await reader.GetFieldValueAsync<T>(0, token);
    }
}

public record DeduplicatedOrder(string Name);

public record DeduplicatedOrderCreated(Guid Id) : CreationResponse($"/deduplicated-response/orders/{Id}");

public record DeduplicatedOrderPlaced(Guid Id);

public static class DeduplicatedResponseEndpoints
{
    public const string Throw = "throw";
    public const string Missing = "missing";
    public const string Slow = "slow";

    private static readonly Dictionary<string, int> _runs = new();
    private static TaskCompletionSource _slowArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource _slowReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task SlowRequestArrived => _slowArrived.Task;

    public static void ReleaseSlowRequest() => _slowReleased.TrySetResult();

    public static void Reset()
    {
        lock (_runs) _runs.Clear();
        _slowArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _slowReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static int Runs(string name)
    {
        lock (_runs) return _runs.GetValueOrDefault(name);
    }

    internal static void Count(string name)
    {
        lock (_runs) _runs[name] = _runs.GetValueOrDefault(name) + 1;
    }

    private static DeduplicatedOrderCreated run(DeduplicatedOrder request)
    {
        Count(request.Name);
        return new DeduplicatedOrderCreated(Guid.NewGuid());
    }

    public static ProblemDetails Validate(DeduplicatedOrder request)
        => request.Name == Missing
            ? new ProblemDetails { Detail = "No such thing", Status = 404 }
            : WolverineContinue.NoProblems;

    // The cascaded message makes the chain flush, to check the order against.
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/deduplicated-response/orders")]
    public static async Task<(DeduplicatedOrderCreated, DeduplicatedOrderPlaced)> PostOrder(DeduplicatedOrder request)
    {
        if (request.Name == Throw) throw new InvalidOperationException("The endpoint failed");

        if (request.Name == Slow)
        {
            _slowArrived.TrySetResult();
            await _slowReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var created = run(request);
        return (created, new DeduplicatedOrderPlaced(created.Id));
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/deduplicated-response/elsewhere")]
    public static DeduplicatedOrderCreated PostElsewhere(DeduplicatedOrder request) => run(request);

    [DeduplicatedWithResponse(DeduplicationScope.Endpoint)]
    [WolverinePost("/deduplicated-response/endpoint/{name}")]
    public static DeduplicatedOrderCreated PostEndpoint(string name, DeduplicatedOrder request) => run(request);

    [DeduplicatedWithResponse(DeduplicationScope.User, WindowInSeconds = 60)]
    [WolverinePost("/deduplicated-response/windowed")]
    public static DeduplicatedOrderCreated PostWindowed(DeduplicatedOrder request) => run(request);

    [DeduplicatedWithResponse(DeduplicationScope.Tenant)]
    [WolverinePost("/deduplicated-response/tenant")]
    public static DeduplicatedOrderCreated PostTenant(DeduplicatedOrder request) => run(request);

    [DeduplicatedWithResponse(DeduplicationScope.User, Required = false)]
    [WolverinePost("/deduplicated-response/optional")]
    public static DeduplicatedOrderCreated PostOptional(DeduplicatedOrder request) => run(request);

    [DeduplicatedWithResponse(DeduplicationScope.User, Key = "X-Request-Key")]
    [WolverinePost("/deduplicated-response/other-header")]
    public static DeduplicatedOrderCreated PostOtherHeader(DeduplicatedOrder request) => run(request);

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/deduplicated-response/text")]
    public static string PostText(DeduplicatedOrder request) => request.Name;
}

// Its own class: the Validate middleware above needs a body.
public static class MissingResourceDeduplicatedEndpoint
{
    [DeduplicatedWithResponse(DeduplicationScope.User), NoContentIfMissing]
    [WolverineGet("/deduplicated-response/maybe")]
    public static DeduplicatedOrderCreated? Get()
    {
        DeduplicatedResponseEndpoints.Count("maybe");
        return null;
    }
}

public static class EarlyExitDeduplicatedEndpoint
{
    public const string Early = "early";

    public static IResult Before(DeduplicatedOrder request)
        => request.Name == Early ? Results.Accepted() : WolverineContinue.Result();

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/deduplicated-response/early-exit")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
}
