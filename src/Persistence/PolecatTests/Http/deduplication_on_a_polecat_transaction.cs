using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Linq;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Http;
using Wolverine.Polecat;
using Wolverine.Runtime;

namespace PolecatTests.Http;

/// <summary>
/// GH-4605, the Polecat mirror of <c>Wolverine.Http.Tests/Marten/deduplication_on_a_marten_transaction</c>:
/// a <c>[Deduplicated]</c> HTTP endpoint that commits through a Polecat session and refuses some requests
/// with a non-2xx <c>ProblemDetails</c> rather than by throwing.
/// </summary>
/// <remarks>
/// <para>
/// <c>HttpChain.AssembleTypes</c> calls <c>ApplyDeduplication</c> for every provider, and the HTTP frames
/// themselves are provider-agnostic — so this is confirming the wiring rather than chasing a suspected
/// bug. It is still worth pinning, because HTTP is where GH-4547's ordering problem lived: the claim was
/// released in a <c>finally</c> that ran after the failure response had already been flushed, so a prompt
/// retry could beat the DELETE. The transactional branch is precisely the one that removes the release,
/// and a release that is not emitted cannot be mis-ordered.
/// </para>
/// <para>
/// Its own host rather than a shared one: <c>[Deduplicated]</c> needs
/// <c>Durability.EnableMessageDeduplication</c>, which provisions a table.
/// </para>
/// </remarks>
public class deduplication_on_a_polecat_transaction : IAsyncLifetime
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

            // Wolverine caches the detected application assembly process-wide, so a host built after
            // another fixture in this assembly inherits THAT application assembly and never sees the
            // endpoints below -- every request 404s. Passes in isolation, fails in the full suite.
            opts.Discovery.IncludeAssembly(typeof(deduplication_on_a_polecat_transaction).Assembly);
        });

        builder.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = SchemaName;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a transactional deduplication test endpoint",
                    type => type != typeof(PolecatDeduplicatedEndpoint)))));

        await ((IHost)theHost).ResetResourceState();

        // ResetResourceState clears Wolverine's own tables -- the deduplication table included -- but
        // leaves the application's Polecat documents alone, and the assertions below COUNT those documents.
        // Without this the class passes on a virgin database and fails on every rerun.
        await ((IHost)theHost).DocumentStore().Advanced.Clean.DeleteAllDocumentsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task a_non_2xx_answer_never_claimed_the_id()
    {
        // The Validate method refuses with a 404, the handler never runs, and nothing was written -- so a
        // caller who never saw the failure and retries under the same key must be able to do the work.
        await theHost.Scenario(x =>
        {
            x.Post.Json(new PolecatDedupRequest("missing")).ToUrl("/polecat-dedup/create");
            x.WithRequestHeader("Idempotency-Key", "tx-retry-1");
            x.StatusCodeShouldBe(404);
        });

        // No delay, no polling. Unlike the claim-and-release path, there is nothing racing here to wait
        // for: the claim only exists if the transaction committed, and it did not.
        (await claimCountAsync("tx-retry-1")).ShouldBe(0);

        await theHost.Scenario(x =>
        {
            x.Post.Json(new PolecatDedupRequest("real")).ToUrl("/polecat-dedup/create");
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
            x.Post.Json(new PolecatDedupRequest("kept")).ToUrl("/polecat-dedup/create");
            x.WithRequestHeader("Idempotency-Key", "tx-kept-1");
            x.StatusCodeShouldBeOk();
        });

        await theHost.Scenario(x =>
        {
            x.Post.Json(new PolecatDedupRequest("kept-again")).ToUrl("/polecat-dedup/create");
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
            x.Post.Json(new PolecatDedupRequest("warmup")).ToUrl("/polecat-dedup/create");
            x.WithRequestHeader("Idempotency-Key", Guid.NewGuid().ToString());
            x.StatusCodeShouldBeOk();
        });

        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.Method.HandlerType == typeof(PolecatDeduplicatedEndpoint));

        var source = chain.SourceCode.ShouldNotBeNull();

        // GH-4547's OnStarting registration and GH-4501's status-code test both exist only to make a
        // compensating release safe. Neither should be here -- there is no release.
        source.ShouldNotContain(nameof(HttpHandler.ReleaseDeduplicationClaimBeforeFailureResponse));
        source.ShouldNotContain("ReleaseAsync");
        source.ShouldNotContain("StatusCode >= 400");

        source.ShouldContain($"{nameof(IPolecatDeduplicator.QueueClaim)}(documentSession");
    }

    private static async Task<int> claimCountAsync(string key)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from {SchemaName}.wolverine_deduplication where deduplication_id = @id";
        cmd.Parameters.AddWithValue("@id", key);

        return (int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<int> countAsync(string name)
    {
        await using var session = ((IHost)theHost).DocumentStore().QuerySession();
        return await session.Query<PolecatDedupDocument>()
            .CountAsync(x => x.Name == name, TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// GH-4605. The negative half: an endpoint that has a Polecat session but never commits one keeps the
/// claim-and-release path.
/// </summary>
/// <remarks>
/// This is what keeps the fixture above from being vacuous. "Can this provider own the chain's
/// transaction?" is true for any chain that so much as takes an <c>IDocumentSession</c>, and a claim
/// enlisted on a unit of work that is never saved is simply never written — so the endpoint reports itself
/// as deduplicated and every single replay runs. No exception, no log line, and every source-level
/// assertion in the class above still passes.
/// </remarks>
public class polecat_deduplication_without_a_commit_keeps_the_release : IAsyncLifetime
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

            // Deliberately NOT AutoApplyTransactions: this endpoint manages its own commit, so nothing
            // adds a SaveChangesAsync postprocessor for a claim to ride.
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(
                typeof(polecat_deduplication_without_a_commit_keeps_the_release).Assembly);
        });

        builder.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "http_no_commit_dedup";
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the uncommitted deduplication test endpoint",
                    type => type != typeof(UncommittedPolecatDeduplicatedEndpoint)))));

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
            x.Post.Json(new PolecatDedupRequest("once")).ToUrl("/polecat-dedup/uncommitted");
            x.WithRequestHeader("Idempotency-Key", "no-commit-1");
            x.StatusCodeShouldBeOk();
        });

        await theHost.Scenario(x =>
        {
            x.Post.Json(new PolecatDedupRequest("twice")).ToUrl("/polecat-dedup/uncommitted");
            x.WithRequestHeader("Idempotency-Key", "no-commit-1");
            x.StatusCodeShouldBe(409);
        });
    }

    [Fact]
    public async Task and_it_took_the_claim_and_release_path_to_do_it()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new PolecatDedupRequest("warmup")).ToUrl("/polecat-dedup/uncommitted");
            x.WithRequestHeader("Idempotency-Key", Guid.NewGuid().ToString());
            x.StatusCodeShouldBeOk();
        });

        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.Method.HandlerType == typeof(UncommittedPolecatDeduplicatedEndpoint));

        var source = chain.SourceCode.ShouldNotBeNull();

        source.ShouldContain("TryClaimAsync");
        source.ShouldContain("ReleaseAsync");
        source.ShouldNotContain(nameof(IPolecatDeduplicator.QueueClaim));
    }
}

public record PolecatDedupRequest(string Name);

public class PolecatDedupDocument
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public static class PolecatDeduplicatedEndpoint
{
    public static ProblemDetails Validate(PolecatDedupRequest request)
    {
        return request.Name == "missing"
            ? new ProblemDetails { Detail = "No such thing", Status = 404 }
            : WolverineContinue.NoProblems;
    }

    [Deduplicated]
    [WolverinePost("/polecat-dedup/create")]
    public static string Post(PolecatDedupRequest request, IDocumentSession session)
    {
        session.Store(new PolecatDedupDocument { Id = Guid.NewGuid(), Name = request.Name });
        return "ok";
    }
}

/// <summary>
/// Commits for itself, so nothing appends a <c>SaveChangesAsync</c> postprocessor for an enlisted claim to
/// ride. See <see cref="polecat_deduplication_without_a_commit_keeps_the_release" />.
/// </summary>
public static class UncommittedPolecatDeduplicatedEndpoint
{
    [Deduplicated]
    [WolverinePost("/polecat-dedup/uncommitted")]
    public static async Task<string> Post(PolecatDedupRequest request, IDocumentSession session,
        CancellationToken cancellation)
    {
        session.Store(new PolecatDedupDocument { Id = Guid.NewGuid(), Name = request.Name });
        await session.SaveChangesAsync(cancellation);
        return "ok";
    }
}
