using System.Collections.Concurrent;
using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using JasperFx.Events;
using Wolverine.Persistence.EventSourcing;
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
/// <c>Durability.MessageDeduplicationMode</c>, which provisions a table, and turning that on for every
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
            opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
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

            // Fails a commit after the database has committed it, for one endpoint only
            m.Listeners.Add(new FailAfterCommitListener());
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        // The shared WolverineWebApi assembly is on the scan path and its endpoints need registrations this
        // host deliberately does not have.
        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a transactional deduplication test endpoint",
                    type => type != typeof(MartenDeduplicatedEndpoint) &&
                            type != typeof(MartenDeduplicatedAggregateEndpoint)))));

        await ((IHost)theHost).ResetResourceState();

        // ResetResourceState clears Wolverine's own tables -- the deduplication table included -- but
        // leaves the application's Marten documents alone, and the assertions below COUNT those documents.
        // Without this the class passes on a virgin database and fails on every rerun, which is the worst
        // shape a test can have: green in CI, red only on the machine of whoever next touches this code.
        await theHost.Services.GetRequiredService<IDocumentStore>()
            .Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(MartenDedupDocument));
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

    [Fact]
    public async Task a_concurrent_repeat_that_fails_on_the_stream_version_is_refused_with_409()
    {
        // GH-5166. Both requests load the stream at the same version before either appends. The loser's commit fails
        // on the stream version before it reaches the claim's INSERT, so the unique-violation classifier never saw
        // it, and it escaped as a 500
        var counterId = await startCounterAsync();
        var key = Guid.NewGuid().ToString();
        var command = new IncrementDeduplicatedCounter(counterId, "race-" + key);

        var results = await Task.WhenAll(
            postAnyAsync("/marten-dedup/aggregate-race", command, key),
            postAnyAsync("/marten-dedup/aggregate-race", command, key));

        MartenDeduplicatedAggregateEndpoint.RaceReached(command.Note).ShouldBe(2);
        results.Select(x => x.Context.Response.StatusCode).Order().ShouldBe([200, 409]);

        (await incrementsAsync(counterId)).ShouldBe(1);
        (await claimCountAsync(key)).ShouldBe(1);
    }

    [Fact]
    public async Task a_write_aggregate_of_the_aggregate_itself_claims_in_the_transaction()
    {
        // GH-????. [WriteAggregate] binds the aggregate rather than its IEventStream<T>, so nothing in the signature
        // names Marten, and the workflow applies the Marten transaction itself
        var counterId = await startCounterAsync();
        var key = Guid.NewGuid().ToString();

        (await postAnyAsync("/marten-dedup/aggregate", new IncrementDeduplicatedCounter(counterId, "once"), key))
            .Context.Response.StatusCode.ShouldBe(200);
        (await postAnyAsync("/marten-dedup/aggregate", new IncrementDeduplicatedCounter(counterId, "twice"), key))
            .Context.Response.StatusCode.ShouldBe(409);

        (await incrementsAsync(counterId)).ShouldBe(1);

        var source = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == "/marten-dedup/aggregate").SourceCode.ShouldNotBeNull();
        source.ShouldContain($"{nameof(IMartenDeduplicator.QueueClaim)}(documentSession");
        source.ShouldNotContain("TryClaimAsync");
    }

    [Fact]
    public async Task a_commit_that_fails_with_no_concurrent_claim_is_rethrown_and_claims_nothing()
    {
        // GH-5166, the other half: a failed commit is only a lost race when the id is claimed afterwards
        var key = Guid.NewGuid().ToString();

        await failingPostAsync("/marten-dedup/failing-commit", new MartenDedupRequest("failing-" + key), key);

        (await claimCountAsync(key)).ShouldBe(0);
        (await countAsync("failing-" + key)).ShouldBe(0);
    }

    [Fact]
    public async Task a_failure_after_the_database_commit_is_not_answered_as_a_lost_race()
    {
        // GH-5166. A session listener throws inside SaveChangesAsync after the database committed: the id is claimed,
        // by this request. That is not a concurrent caller's win, so it is not looked up and is rethrown
        var key = Guid.NewGuid().ToString();
        var name = "after-commit-" + key;

        await failingPostAsync("/marten-dedup/after-commit-failure", new MartenDedupRequest(name), key);

        (await claimCountAsync(key)).ShouldBe(1);
        (await countAsync(name)).ShouldBe(1);

        await theHost.Scenario(x =>
        {
            x.Post.Json(new MartenDedupRequest(name)).ToUrl("/marten-dedup/after-commit-failure");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(409);
        });
    }

    private async Task<Guid> startCounterAsync()
    {
        var counterId = Guid.NewGuid();
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Events.StartStream<DeduplicatedCounter>(counterId, new DeduplicatedCounterStarted());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return counterId;
    }

    private async Task<int> incrementsAsync(Guid counterId)
    {
        await using var query = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var events = await query.Events.FetchStreamAsync(counterId, token: TestContext.Current.CancellationToken);
        return events.Count(x => x.Data is DeduplicatedCounterIncremented);
    }

    // As deduplicated_with_response_on_marten.failingPostAsync: a 500 under the developer exception page, else the throw
    private async Task failingPostAsync<T>(string url, T body, string key) where T : class
    {
        try
        {
            var result = await postAnyAsync(url, body, key);
            result.Context.Response.StatusCode.ShouldBeGreaterThanOrEqualTo(500);
        }
        catch (Exception e) when (e is InvalidOperationException or global::Marten.Exceptions.MartenCommandException)
        {
            // Propagated straight through the test server
        }
    }

    private Task<IScenarioResult> postAnyAsync<T>(string url, T body, string key) where T : class
        => theHost.Scenario(x =>
        {
            x.Post.Json(body).ToUrl(url);
            x.WithRequestHeader("Idempotency-Key", key);
            x.IgnoreStatusCode();
        });

    private static Task<long> claimCountAsync(string key)
        => scalarAsync($"select count(*) from {SchemaName}.wolverine_deduplication_hashed where deduplication_id = @id",
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
            opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
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

/// <summary>GH-5166. [Deduplicated] endpoints whose commit can fail on something other than the claim.</summary>
public static class MartenDeduplicatedAggregateEndpoint
{
    private static readonly ConcurrentDictionary<string, int> _raceReached = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _raceBarriers = new();

    public static int RaceReached(string note) => _raceReached.GetValueOrDefault(note);

    [Deduplicated]
    [WolverinePost("/marten-dedup/aggregate-race")]
    public static async Task<DeduplicatedCounterIncremented> PostAggregate(IncrementDeduplicatedCounter command,
        [WriteAggregate] IEventStream<DeduplicatedCounter> stream)
    {
        // Both have loaded the same version before either appends
        var barrier = _raceBarriers.GetOrAdd(command.Note,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_raceReached.AddOrUpdate(command.Note, 1, (_, count) => count + 1) == 2) barrier.TrySetResult();

        // Bounded, so a regression fails the assertions rather than hanging the test
        await Task.WhenAny(barrier.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        var incremented = new DeduplicatedCounterIncremented(command.Note);
        stream.AppendOne(incremented);
        return incremented;
    }

    [Deduplicated]
    [WolverinePost("/marten-dedup/aggregate")]
    public static (DeduplicatedCounterIncremented, Events) PostAggregateItself(IncrementDeduplicatedCounter command,
        [WriteAggregate] DeduplicatedCounter counter)
    {
        var incremented = new DeduplicatedCounterIncremented(command.Note);
        return (incremented, [incremented]);
    }

    [Deduplicated]
    [WolverinePost("/marten-dedup/failing-commit")]
    public static string PostFailingCommit(MartenDedupRequest request, IDocumentSession session)
    {
        session.Store(new MartenDedupDocument { Id = Guid.NewGuid(), Name = request.Name });

        // Fails inside SaveChangesAsync, with no concurrent request to have committed a claim
        session.QueueSqlCommand("select 1 / 0");
        return "ok";
    }

    [Deduplicated]
    [WolverinePost("/marten-dedup/after-commit-failure")]
    public static string PostAfterCommitFailure(MartenDedupRequest request, IDocumentSession session)
    {
        session.Store(new MartenDedupDocument { Id = Guid.NewGuid(), Name = request.Name });
        session.Store(new FailAfterCommitMarker { Id = Guid.NewGuid() });
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
