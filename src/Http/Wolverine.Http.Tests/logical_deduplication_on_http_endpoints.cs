using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
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
                    type => type != typeof(DeduplicatedEndpoint) && type != typeof(BenignReplayEndpoint)))));

        await ((IHost)theHost).ResetResourceState();

        DeduplicatedEndpoint.Calls.Clear();
        BenignReplayEndpoint.Calls.Clear();
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
