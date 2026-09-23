using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-4505 over HTTP, on the exact composition GH-4501 was reported against: a <c>[Deduplicated]</c>
/// endpoint that commits through a Marten session and refuses some requests with a non-2xx
/// <c>ProblemDetails</c> rather than by throwing.
/// </summary>
/// <remarks>
/// <para>
/// GH-4501 fixed that by releasing the claim in a <c>finally</c> keyed on the response status, and GH-4547
/// then had to move the release earlier still, into <c>Response.OnStarting</c>, because the 404 was flushed
/// before the <c>finally</c> ran and a prompt retry could beat the DELETE. Both of those exist because the
/// claim was committed on a connection of its own. Here it is not: the refusal returns before
/// <c>SaveChangesAsync</c>, so the claim was never written and there is no window to narrow.
/// </para>
/// <para>
/// Its own host rather than the shared sample app: <c>[Deduplicated]</c> needs
/// <c>Durability.EnableMessageDeduplication</c>, which provisions a table, and turning that on for every
/// endpoint in <c>WolverineWebApi</c> would change what the rest of the suite is testing.
/// </para>
/// </remarks>
public class deduplication_on_a_marten_transaction : IAsyncLifetime
{
    private const string SchemaName = "http_tx_dedup";

    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.EnableMessageDeduplication = true;
            opts.Durability.DeduplicationWindow = 1.Hours();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Policies.AutoApplyTransactions();

            // Wolverine caches the detected application assembly process-wide, so a host built after the
            // shared WolverineWebApi fixture inherits THAT application assembly and never sees the
            // endpoints below -- every request 404s. Passes in isolation, fails in the full suite.
            opts.Discovery.IncludeAssembly(typeof(deduplication_on_a_marten_transaction).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = SchemaName;
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        // The shared WolverineWebApi assembly is on the scan path and its endpoints need registrations this
        // host deliberately does not have.
        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a transactional deduplication test endpoint",
                    type => type != typeof(MartenDeduplicatedEndpoint)))));

        await ((IHost)theHost).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task a_non_2xx_answer_never_claimed_the_id()
    {
        // The reported failure. The Validate method refuses with a 404, the handler never runs, and nothing
        // was written -- so a caller who never saw the failure and retries under the same key must be able
        // to do the work.
        await theHost.Scenario(x =>
        {
            x.Post.Json(new MartenDedupRequest("missing")).ToUrl("/marten-dedup/create");
            x.WithRequestHeader("Idempotency-Key", "tx-retry-1");
            x.StatusCodeShouldBe(404);
        });

        // No delay, no polling. Unlike the claim-and-release path, there is nothing racing here to wait
        // for: the claim only exists if the transaction committed, and it did not.
        (await claimCountAsync("tx-retry-1")).ShouldBe(0);

        await theHost.Scenario(x =>
        {
            x.Post.Json(new MartenDedupRequest("real")).ToUrl("/marten-dedup/create");
            x.WithRequestHeader("Idempotency-Key", "tx-retry-1");
            x.StatusCodeShouldBeOk();
        });

        (await countAsync("real")).ShouldBe(1);
    }

    [Fact]
    public async Task a_successful_request_keeps_its_claim_and_the_replay_is_refused()
    {
        // The other half of the same rule: refusing on anything short of a 2xx must not turn into
        // refusing on everything.
        await theHost.Scenario(x =>
        {
            x.Post.Json(new MartenDedupRequest("kept")).ToUrl("/marten-dedup/create");
            x.WithRequestHeader("Idempotency-Key", "tx-kept-1");
            x.StatusCodeShouldBeOk();
        });

        await theHost.Scenario(x =>
        {
            x.Post.Json(new MartenDedupRequest("kept-again")).ToUrl("/marten-dedup/create");
            x.WithRequestHeader("Idempotency-Key", "tx-kept-1");
            x.StatusCodeShouldBe(409);
        });

        (await countAsync("kept")).ShouldBe(1);
        (await countAsync("kept-again")).ShouldBe(0);
        (await claimCountAsync("tx-kept-1")).ShouldBe(1);
    }

    [Fact]
    public async Task the_generated_endpoint_carries_no_compensating_release()
    {
        // Warm the chain so codegen has run.
        await theHost.Scenario(x =>
        {
            x.Post.Json(new MartenDedupRequest("warmup")).ToUrl("/marten-dedup/create");
            x.WithRequestHeader("Idempotency-Key", Guid.NewGuid().ToString());
            x.StatusCodeShouldBeOk();
        });

        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.Method.HandlerType == typeof(MartenDeduplicatedEndpoint));

        var source = chain.SourceCode.ShouldNotBeNull();

        // GH-4547's OnStarting registration and GH-4501's status-code test both exist only to make a
        // compensating release safe. Neither should be here -- there is no release.
        source.ShouldNotContain(nameof(HttpHandler.ReleaseDeduplicationClaimBeforeFailureResponse));
        source.ShouldNotContain("ReleaseAsync");
        source.ShouldNotContain("StatusCode >= 400");

        source.ShouldContain($"{nameof(IMartenDeduplicator.QueueClaim)}(documentSession");
    }

    private static Task<long> claimCountAsync(string key)
        => scalarAsync($"select count(*) from {SchemaName}.wolverine_deduplication where deduplication_id = @id",
            key);

    private static Task<long> countAsync(string name)
        => scalarAsync($"select count(*) from {SchemaName}.mt_doc_martendedupdocument where data ->> 'Name' = @id",
            name);

    private static async Task<long> scalarAsync(string sql, string id)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", id);

        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

/// <summary>
/// GH-4505. The negative half: an endpoint that has a Marten session but never commits one keeps the
/// claim-and-release path.
/// </summary>
/// <remarks>
/// Worth its own host, because getting this wrong is silent and total. "Can this provider own the chain's
/// transaction?" is true for any chain that so much as takes an <c>IDocumentSession</c>, and a claim queued
/// onto a unit of work that is never saved is simply never written — so the endpoint reports itself as
/// deduplicated and every single replay runs. No exception, no log line, and every source-level assertion
/// in the class above still passes.
/// </remarks>
public class deduplication_without_a_commit_keeps_the_release : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.EnableMessageDeduplication = true;
            opts.Durability.DeduplicationWindow = 1.Hours();

            // Deliberately NOT AutoApplyTransactions: this endpoint manages its own commit, so nothing adds
            // a SaveChangesAsync postprocessor for a claim to ride.
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(deduplication_without_a_commit_keeps_the_release).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "http_no_commit_dedup";
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the uncommitted deduplication test endpoint",
                    type => type != typeof(UncommittedDeduplicatedEndpoint)))));

        await ((IHost)theHost).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task the_replay_is_still_refused()
    {
        // The behaviour that must survive the fallback, asserted first: whichever path this endpoint took,
        // it has to actually deduplicate.
        await theHost.Scenario(x =>
        {
            x.Post.Json(new MartenDedupRequest("once")).ToUrl("/marten-dedup/uncommitted");
            x.WithRequestHeader("Idempotency-Key", "no-commit-1");
            x.StatusCodeShouldBeOk();
        });

        await theHost.Scenario(x =>
        {
            x.Post.Json(new MartenDedupRequest("twice")).ToUrl("/marten-dedup/uncommitted");
            x.WithRequestHeader("Idempotency-Key", "no-commit-1");
            x.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task and_it_took_the_claim_and_release_path_to_do_it()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new MartenDedupRequest("warmup")).ToUrl("/marten-dedup/uncommitted");
            x.WithRequestHeader("Idempotency-Key", Guid.NewGuid().ToString());
            x.StatusCodeShouldBeOk();
        });

        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.Method.HandlerType == typeof(UncommittedDeduplicatedEndpoint));

        var source = chain.SourceCode.ShouldNotBeNull();

        source.ShouldContain("TryClaimAsync");
        source.ShouldContain("ReleaseAsync");
        source.ShouldNotContain(nameof(IMartenDeduplicator.QueueClaim));
    }
}

public record MartenDedupRequest(string Name);

public class MartenDedupDocument
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public static class MartenDeduplicatedEndpoint
{
    public static ProblemDetails Validate(MartenDedupRequest request)
    {
        return request.Name == "missing"
            ? new ProblemDetails { Detail = "No such thing", Status = 404 }
            : WolverineContinue.NoProblems;
    }

    [Deduplicated]
    [WolverinePost("/marten-dedup/create")]
    public static string Post(MartenDedupRequest request, IDocumentSession session)
    {
        session.Store(new MartenDedupDocument { Id = Guid.NewGuid(), Name = request.Name });
        return "ok";
    }
}

/// <summary>
/// Commits for itself, so nothing appends a <c>SaveChangesAsync</c> postprocessor for a queued claim to
/// ride. See <see cref="deduplication_without_a_commit_keeps_the_release" />.
/// </summary>
public static class UncommittedDeduplicatedEndpoint
{
    [Deduplicated]
    [WolverinePost("/marten-dedup/uncommitted")]
    public static async Task<string> Post(MartenDedupRequest request, IDocumentSession session,
        CancellationToken cancellation)
    {
        session.Store(new MartenDedupDocument { Id = Guid.NewGuid(), Name = request.Name });
        await session.SaveChangesAsync(cancellation);
        return "ok";
    }
}
