using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-4180. Logical deduplication on Wolverine.HTTP endpoints.
///
/// <para>
/// An HTTP endpoint has no incoming <c>Envelope</c>, so unlike a message handler it cannot take its
/// logical id from <c>Envelope.DeduplicationId</c>. It reads the conventional <c>Idempotency-Key</c>
/// request header instead — the same header Stripe, Adyen and the IETF draft already use, so a
/// caller that already sends one gets deduplication with no extra configuration.
/// </para>
///
/// <para>
/// And unlike a message handler, an endpoint owes its caller an answer, so a refusal is a status
/// code rather than a silent discard. That also gives HTTP the request/reply half of idempotency for
/// free, without Wolverine storing and replaying the original response.
/// </para>
/// </summary>
public class logical_deduplication_on_http_endpoints : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "http_dedup");

            opts.Durability.EnableMessageDeduplication = true;
            opts.Durability.DeduplicationWindow = 1.Hours();

            opts.Discovery.DisableConventionalDiscovery();

            // Pin this assembly into the scan set. Wolverine caches the detected application assembly
            // process-wide, so when this class runs after the shared WolverineWebApi-based fixture, this
            // host inherits THAT application assembly and never sees the endpoints below -- every request
            // 404s. The tests pass in isolation and fail in the full suite without this line.
            opts.Discovery.IncludeAssembly(typeof(logical_deduplication_on_http_endpoints).Assembly);
        });

        builder.Services.AddWolverineHttp();

        // Narrow HTTP discovery to just this file's endpoints. The shared WolverineWebApi assembly is on
        // the scan path and its endpoints need Marten/EF Core registrations this host deliberately does
        // not have -- standing all that up would be testing those integrations, not deduplication.
        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a deduplication test endpoint",
                    type => type != typeof(DeduplicatedEndpoint)
                            && type != typeof(BenignReplayEndpoint)
                            && type != typeof(RefusingDeduplicatedEndpoint)
                            && type != typeof(TransactionalDeduplicatedEndpoint)))));

        await ((IHost)theHost).ResetResourceState();

        DeduplicatedEndpoint.Calls.Clear();
        BenignReplayEndpoint.Calls.Clear();
        RefusingDeduplicatedEndpoint.Calls.Clear();
        TransactionalDeduplicatedEndpoint.Calls.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task first_request_runs_and_the_replay_is_refused_with_409()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("first")).ToUrl("/dedup/create");
            x.WithRequestHeader("Idempotency-Key", "order-123");
            x.StatusCodeShouldBeOk();
        });

        // Same key, different body. Nothing but the logical id can refuse this one.
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("second")).ToUrl("/dedup/create");
            x.WithRequestHeader("Idempotency-Key", "order-123");
            x.StatusCodeShouldBe(409);
        });

        DeduplicatedEndpoint.Calls.ShouldHaveSingleItem().ShouldBe("first");
    }

    [Fact]
    public async Task different_keys_both_run()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("a")).ToUrl("/dedup/create");
            x.WithRequestHeader("Idempotency-Key", "order-a");
            x.StatusCodeShouldBeOk();
        });

        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("b")).ToUrl("/dedup/create");
            x.WithRequestHeader("Idempotency-Key", "order-b");
            x.StatusCodeShouldBeOk();
        });

        DeduplicatedEndpoint.Calls.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task a_missing_required_key_is_a_400_rather_than_a_silent_pass()
    {
        // Nothing has been done and nothing will be, so this must be visible to the caller. Passing an
        // unkeyed request through would report the endpoint as protected while every duplicate ran.
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("no key")).ToUrl("/dedup/create");
            x.StatusCodeShouldBe(400);
        });

        DeduplicatedEndpoint.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_replay_can_be_configured_as_benign_instead_of_a_conflict()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("once")).ToUrl("/dedup/benign");
            x.WithRequestHeader("Idempotency-Key", "benign-1");
            x.StatusCodeShouldBeOk();
        });

        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("twice")).ToUrl("/dedup/benign");
            x.WithRequestHeader("Idempotency-Key", "benign-1");
            x.StatusCodeShouldBe(204);
        });

        BenignReplayEndpoint.Calls.ShouldHaveSingleItem();
    }

    // GH-4501. An idempotency key is supposed to mean "this succeeded once", not "this was attempted
    // once". A chain that stops on a non-2xx outcome -- a ProblemDetails 400 from a Validate method, a
    // FluentValidation failure, a [WriteAggregate] 404 on a missing stream -- did no work, and it is not
    // a throw, so the compensating release on the exception path never runs. The claim survives, and the
    // caller that never saw the failure and retries under the same key is told "already done" for work
    // that never happened.
    [Fact]
    public async Task a_non_2xx_answer_releases_the_claim()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("missing")).ToUrl("/dedup/refusing");
            x.WithRequestHeader("Idempotency-Key", "retry-1");
            x.StatusCodeShouldBe(404);
        });

        RefusingDeduplicatedEndpoint.Calls.ShouldBeEmpty();

        // Same key, and this time the request is good. Nothing has ever been done under this key, so it
        // has to run.
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("real")).ToUrl("/dedup/refusing");
            x.WithRequestHeader("Idempotency-Key", "retry-1");
            x.StatusCodeShouldBeOk();
        });

        RefusingDeduplicatedEndpoint.Calls.ShouldHaveSingleItem().ShouldBe("real");
    }

    // GH-4547. The regression test for the RACE, as opposed to the behaviour: the claim has to be gone
    // by the time the caller can see the failure response, not merely gone eventually.
    //
    // GH-4501 released it in a finally keyed on the response status, which is correct but runs AFTER
    // WriteProblems has flushed the 404. Reading the claim table the instant the response returns used to
    // show the row still there (and gone a few hundred milliseconds later), so a client retrying promptly
    // under the same key was refused as a duplicate for work that never happened. That is the failure
    // GH-4501 existed to remove, moved rather than fixed -- and it is what made
    // a_non_2xx_answer_releases_the_claim fail under full suite load while passing in isolation.
    [Fact]
    public async Task the_claim_is_already_released_when_the_failure_response_arrives()
    {
        const string key = "race-1";

        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("missing")).ToUrl("/dedup/refusing");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(404);
        });

        // No delay, no retry, no polling. The whole point is that the caller does not have to wait.
        (await claimCountAsync(key)).ShouldBe(0,
            "the deduplication claim must be released before the failure response reaches the caller");
    }

    // The behavioural test above is the one that matters, but its window is one database round trip --
    // narrow enough that simply opening a connection to look can outlast it, which is exactly why the
    // original bug read as a flaky test rather than a broken guarantee. This pins the ORDERING itself, so
    // the fix cannot regress silently even when the timing happens to be forgiving.
    [Fact]
    public async Task the_release_is_registered_before_the_response_can_be_flushed()
    {
        // Warm the chain so codegen has run
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("real")).ToUrl("/dedup/refusing");
            x.WithRequestHeader("Idempotency-Key", Guid.NewGuid().ToString());
            x.StatusCodeShouldBeOk();
        });

        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.Method.HandlerType == typeof(RefusingDeduplicatedEndpoint));

        var source = chain.SourceCode.ShouldNotBeNull();

        var registration = source.IndexOf(
            nameof(HttpHandler.ReleaseDeduplicationClaimBeforeFailureResponse), StringComparison.Ordinal);
        registration.ShouldBeGreaterThan(-1,
            "the claim release must be registered to run before the response is flushed");

        // ...and it has to be registered BEFORE the try block that writes the response, or the callback
        // would be attached too late to matter.
        var tryBlock = source.IndexOf("try", registration, StringComparison.Ordinal);
        tryBlock.ShouldBeGreaterThan(registration);
    }

    private static async Task<long> claimCountAsync(string key)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "select count(*) from http_dedup.wolverine_deduplication where deduplication_id = @id";
        cmd.Parameters.AddWithValue("id", key);

        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // ...and the other half of the same rule: a key that did succeed stays claimed. Releasing on
    // anything short of a 2xx must not turn into releasing on everything.
    [Fact]
    public async Task a_2xx_answer_keeps_the_claim()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("real")).ToUrl("/dedup/refusing");
            x.WithRequestHeader("Idempotency-Key", "kept-1");
            x.StatusCodeShouldBeOk();
        });

        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("real")).ToUrl("/dedup/refusing");
            x.WithRequestHeader("Idempotency-Key", "kept-1");
            x.StatusCodeShouldBe(409);
        });

        RefusingDeduplicatedEndpoint.Calls.ShouldHaveSingleItem();
    }

    // GH-4501, second half. The compensating release used to be emitted ONLY into non-transactional
    // chains, on the reasoning that a transactional chain writes its claim inside the handler's own
    // transaction and a rollback takes the claim with it. Nothing implements that: every
    // IDeduplicationStore Wolverine ships opens its own connection off a DbDataSource, so the claim is
    // committed independently of whatever the handler is doing and survives a rollback intact. Which
    // made the guard exactly backwards — it skipped the release on the chains that needed it, which is
    // the composition the GH-4501 report is running.
    [Fact]
    public async Task a_transactional_chain_releases_its_claim_too()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("missing")).ToUrl("/dedup/transactional");
            x.WithRequestHeader("Idempotency-Key", "tx-1");
            x.StatusCodeShouldBe(404);
        });

        TransactionalDeduplicatedEndpoint.Calls.ShouldBeEmpty();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("real")).ToUrl("/dedup/transactional");
            x.WithRequestHeader("Idempotency-Key", "tx-1");
            x.StatusCodeShouldBeOk();
        });

        TransactionalDeduplicatedEndpoint.Calls.ShouldHaveSingleItem().ShouldBe("real");
    }

    [Fact]
    public async Task the_refusal_status_is_advertised_in_the_endpoint_metadata()
    {
        // A 409 a client can receive but cannot discover from the generated OpenAPI document is a
        // contract change hidden from exactly the people who have to handle it.
        var graph = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/dedup/create");
        chain.ShouldNotBeNull();

        var metadata = chain.BuildEndpoint(RouteWarmup.Lazy)
            .Metadata.OfType<IProducesResponseTypeMetadata>().ToArray();

        metadata.Any(x => x.StatusCode == 409).ShouldBeTrue("the duplicate refusal must be discoverable");
        metadata.Any(x => x.StatusCode == 400).ShouldBeTrue("the missing-key refusal must be discoverable");
    }
}

public record DedupRequest(string Name);

public static class DeduplicatedEndpoint
{
    public static readonly List<string> Calls = [];

    [Deduplicated]
    [WolverinePost("/dedup/create")]
    public static string Post(DedupRequest request)
    {
        Calls.Add(request.Name);
        return "ok";
    }
}

/// <summary>
/// GH-4501. Stands in for the reported shape: an endpoint that refuses a request with a non-2xx
/// <c>ProblemDetails</c> rather than by throwing. The handler never runs, so nothing was done under the
/// idempotency key the request carried.
/// </summary>
public static class RefusingDeduplicatedEndpoint
{
    public static readonly List<string> Calls = [];

    public static ProblemDetails Validate(DedupRequest request)
    {
        return request.Name == "missing"
            ? new ProblemDetails { Detail = "No such thing", Status = 404 }
            : WolverineContinue.NoProblems;
    }

    [Deduplicated]
    [WolverinePost("/dedup/refusing")]
    public static string Post(DedupRequest request)
    {
        Calls.Add(request.Name);
        return "ok";
    }
}

/// <summary>
/// GH-4501, second half. The reported application's endpoint is transactional — a <c>[MartenStore]</c>
/// aggregate endpoint — and the claim does not ride that transaction: every
/// <c>IDeduplicationStore</c> Wolverine ships opens its own connection off a <c>DbDataSource</c>.
/// </summary>
public static class TransactionalDeduplicatedEndpoint
{
    public static readonly List<string> Calls = [];

    public static ProblemDetails Validate(DedupRequest request)
    {
        return request.Name == "missing"
            ? new ProblemDetails { Detail = "No such thing", Status = 404 }
            : WolverineContinue.NoProblems;
    }

    [Transactional]
    [Deduplicated]
    [WolverinePost("/dedup/transactional")]
    public static string Post(DedupRequest request)
    {
        Calls.Add(request.Name);
        return "ok";
    }
}

public static class BenignReplayEndpoint
{
    public static readonly List<string> Calls = [];

    [Deduplicated(DuplicateStatusCode = 204)]
    [WolverinePost("/dedup/benign")]
    public static string Post(DedupRequest request)
    {
        Calls.Add(request.Name);
        return "ok";
    }
}
