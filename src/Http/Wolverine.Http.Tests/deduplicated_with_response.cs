using System.Security.Claims;
using Alba;
using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
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
using Wolverine.Tracking;
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

            // The endpoint's cascaded DeduplicatedOrderPlaced needs a routed handler, or it is only ever
            // recorded as NoRoutes and a_replay_does_not_re_send... could not tell a send from a no-op.
            opts.Discovery.IncludeType<DeduplicatedOrderPlacedHandler>();
        });

        builder.Services.AddWolverineHttp();

        // No tenant id detection, deliberately -- see a_tenant_scope_needs_tenant_detection.
        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a [DeduplicatedWithResponse] test endpoint",
                    type => type != typeof(DeduplicatedResponseEndpoints) && type != typeof(EarlyExitDeduplicatedResponseEndpoint)
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

        // How the endpoint's throw reaches the caller is not this test's subject, and it is not even stable
        // across hosts: WebApplication installs the developer exception page when the environment is
        // Development (which CI sets), turning the throw into a 500; with no exception-handling middleware it
        // propagates out of the test server instead. Accept either, then assert the thing that matters.
        await failingPostAsync(new DeduplicatedOrder(DeduplicatedResponseEndpoints.Throw), key);

        // The claim was released, so the retry is allowed to do the work rather than being refused with 409.
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
        // The refusal is raised while the endpoint's handler is GENERATED (ScopeDeduplicationIdFrame cannot
        // find a tenant id variable), so assert it at the codegen surface. Driving it through a request
        // instead makes the test depend on whether the hosting environment installs an exception page: CI
        // runs Development, where this answers 500 and Alba's own assertion fires first.
        var ex = Should.Throw<InvalidOperationException>(() => compile("/deduplicated-response/tenant"));

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

        await postAsync(new DeduplicatedOrder(EarlyExitDeduplicatedResponseEndpoint.Early), key, 202,
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
    public void a_response_that_is_not_system_text_json_is_refused()
    {
        // Same as a_tenant_scope_needs_tenant_detection: a codegen-time refusal, asserted where it is
        // raised rather than through a request whose surfacing depends on the hosting environment.
        var ex = Should.Throw<NotSupportedException>(() => compile("/deduplicated-response/text"));

        ex.Message.ShouldContain("System.Text.Json");
    }

    [Fact]
    public async Task a_replay_does_not_re_send_the_first_requests_cascaded_messages()
    {
        // PostOrder cascades a DeduplicatedOrderPlaced alongside its response. A replay writes the stored
        // body and returns from the claim frame, so it must never reach the endpoint -- and therefore must
        // never cascade a second time. Runs() already proves the endpoint body ran once; this proves nothing
        // was PUBLISHED twice, which is the half a client would actually feel.
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("cascade-once");

        var host = (IHost)theHost;

        var first = await host.TrackActivity()
            .WaitForMessageToBeReceivedAt<DeduplicatedOrderPlaced>(host)
            .ExecuteAndWaitAsync(_ => postAsync(request, key, 201));

        first.Sent.MessagesOf<DeduplicatedOrderPlaced>().Count().ShouldBe(1);

        var replay = await host.TrackActivity().ExecuteAndWaitAsync(_ => postAsync(request, key, 201));
        replay.Sent.MessagesOf<DeduplicatedOrderPlaced>().ShouldBeEmpty();

        // The 422 branch: a different body under the same key is refused on the claim, ahead of everything.
        var refused = await host.TrackActivity()
            .ExecuteAndWaitAsync(_ => postAsync(new DeduplicatedOrder("cascade-changed"), key, 422));
        refused.Sent.MessagesOf<DeduplicatedOrderPlaced>().ShouldBeEmpty();

        DeduplicatedResponseEndpoints.Runs("cascade-once").ShouldBe(1);
        DeduplicatedResponseEndpoints.Runs("cascade-changed").ShouldBe(0);
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

    /// <summary>
    /// Generates the endpoint's handler, which is where the declaration-time refusals above are raised.
    /// </summary>
    private void compile(string route)
    {
        var graph = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.Chains.Single(x => x.RoutePattern!.RawText == route);

        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, theHost.Services);
    }

    /// <summary>
    /// Posts a request the endpoint is expected to FAIL, tolerating either way that failure can reach a
    /// caller: propagated out of the test server, or turned into a 5xx by an exception-handling middleware
    /// (WebApplication installs the developer exception page when the environment is Development, which CI
    /// sets). Asserts only that it did not succeed.
    /// </summary>
    private async Task failingPostAsync(DeduplicatedOrder body, string key,
        string url = "/deduplicated-response/orders")
    {
        try
        {
            var result = await theHost.Scenario(x =>
            {
                x.Post.Json(body).ToUrl(url);
                x.WithRequestHeader("Idempotency-Key", key);
                x.IgnoreStatusCode();
            });

            result.Context.Response.StatusCode.ShouldBeGreaterThanOrEqualTo(500);
        }
        catch (InvalidOperationException)
        {
            // Propagated straight through the test server: no exception page is installed.
        }
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

// Gives the cascaded DeduplicatedOrderPlaced somewhere to go, so a tracked session can actually count it
// (a message with no routes is only ever recorded as NoRoutes, never as sent). Body is irrelevant.
public class DeduplicatedOrderPlacedHandler
{
    public void Handle(DeduplicatedOrderPlaced _)
    {
    }
}

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

public static class EarlyExitDeduplicatedResponseEndpoint
{
    public const string Early = "early";

    public static IResult Before(DeduplicatedOrder request)
        => request.Name == Early ? Results.Accepted() : WolverineContinue.Result();

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/deduplicated-response/early-exit")]
    public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
}
