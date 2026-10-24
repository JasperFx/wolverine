using System.Collections.Concurrent;
using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Marten.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Http.Runtime;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-4742. [DeduplicatedWithResponse] on endpoints that commit through Marten: the claim and its response are
/// written inside the commit, and the fingerprint still reads a body that the audit frame or aggregate id
/// inference read first.
/// </summary>
public class deduplicated_with_response_on_marten : IAsyncLifetime
{
    private const string SchemaName = "http_deduplicated_response_marten";

    private IAlbaHost theHost = null!;
    private readonly CapturingLoggerProvider theLogs = new();

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Logging.AddProvider(theLogs);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.EnableDeduplicatedResponses = true;
            opts.Durability.DeduplicationWindow = 1.Hours();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Policies.AutoApplyTransactions();

            // The application assembly is cached process-wide; include this one explicitly.
            opts.Discovery.IncludeAssembly(typeof(deduplicated_with_response_on_marten).Assembly);
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

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.TenantId.IsRequestHeaderValue("tenant");

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a Marten [DeduplicatedWithResponse] test endpoint",
                    type => type != typeof(MartenDeduplicatedResponseEndpoints)));
        }));

        await ((IHost)theHost).ResetResourceState();

        // ResetResourceState leaves documents behind, and the tests count them.
        await theHost.Services.GetRequiredService<IDocumentStore>()
            .Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(DeduplicatedOrderDocument));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task a_repeat_is_answered_with_the_first_response_and_the_work_is_committed_once()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("marten-" + key);

        var first = await postAsync("/marten-deduplicated-response/orders", request, key, 201);
        var repeat = await postAsync("/marten-deduplicated-response/orders", request, key, 201);

        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        // Answered before the endpoint ran, not by its commit failing on the claim
        MartenDeduplicatedResponseEndpoints.Runs(request.Name).ShouldBe(1);

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await session.Query<DeduplicatedOrderDocument>().CountAsync(x => x.Name == request.Name,
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task the_claim_and_its_response_are_written_inside_the_commit()
    {
        await postAsync("/marten-deduplicated-response/orders", new DeduplicatedOrder("warm"),
            Guid.NewGuid().ToString(), 201);

        var source = sourceFor("/marten-deduplicated-response/orders");

        // Through the Marten session, as [Deduplicated] checks its claim (GH-4505)
        var find = source.IndexOf(".FindClaimAsync(", StringComparison.Ordinal);
        var queue = source.IndexOf(".QueueClaim(", StringComparison.Ordinal);
        var commit = source.IndexOf("SaveChangesAsync", StringComparison.Ordinal);
        var write = source.IndexOf("WriteJsonAsync", StringComparison.Ordinal);

        find.ShouldBeGreaterThan(-1, source);
        find.ShouldBeLessThan(queue, source);
        queue.ShouldBeLessThan(commit, source);
        commit.ShouldBeLessThan(write, source);

        // Nothing is written outside the transaction, so there is nothing to give back or record afterwards
        source.ShouldNotContain(".TryClaimAsync(");
        source.ShouldNotContain(".RecordResponseAsync(");
        source.ShouldNotContain(".ReleaseUnansweredAsync(");
        source.ShouldNotContain("ReleaseDeduplicatedResponseBeforeFailureResponse");
    }

    [Fact]
    public async Task with_a_write_aggregate_the_claim_lookup_shares_the_batched_query()
    {
        // One round trip for the lookup and FetchForWriting, as [Deduplicated] batches its check (GH-4505)
        var counterId = Guid.NewGuid();
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<DeduplicatedCounter>(counterId, new DeduplicatedCounterStarted());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = Guid.NewGuid().ToString();
        var command = new IncrementDeduplicatedCounter(counterId, "batched-" + key);

        // Both arrive, so the barrier in the endpoint lets them through
        var first = await Task.WhenAll(
            postAsync("/marten-deduplicated-response/counter-race", command, key, 200),
            postAsync("/marten-deduplicated-response/counter-race", command, key, 200));
        var repeat = await postAsync("/marten-deduplicated-response/counter-race", command, key, 200);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first[0].ReadAsTextAsync());

        var source = sourceFor("/marten-deduplicated-response/counter-race");
        source.ShouldContain(".CreateBatchQuery()");
        source.ShouldContain(".ClaimQuery(");
        source.ShouldNotContain(".FindClaimAsync(");
    }

    [Fact]
    public async Task no_claim_exists_while_the_endpoint_runs()
    {
        // What a crash mid-request would leave behind. Claim-and-release has committed its claim by now.
        var key = Guid.NewGuid().ToString();

        await postAsync("/marten-deduplicated-response/observe", new DeduplicatedOrder("observe-" + key), key, 201);

        MartenDeduplicatedResponseEndpoints.ClaimSeenWhileRunning(key).ShouldBeFalse();
        (await findClaimAsync(key)).ShouldNotBeNull().Response.ShouldNotBeNull().StatusCode.ShouldBe(201);
    }

    [Fact]
    public async Task a_failed_endpoint_leaves_no_claim_and_its_retry_runs()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("fail-once-" + key);

        await failingPostAsync("/marten-deduplicated-response/fail-once", request, key);
        (await findClaimAsync(key)).ShouldBeNull();

        await postAsync("/marten-deduplicated-response/fail-once", request, key, 201);

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await session.Query<DeduplicatedOrderDocument>().CountAsync(x => x.Name == request.Name,
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task the_loser_of_a_concurrent_commit_is_answered_with_the_winners_response()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("race-" + key);

        // Both requests find no claim and both do their work before either commits
        var responses = await Task.WhenAll(
            postAsync("/marten-deduplicated-response/race", request, key, 201),
            postAsync("/marten-deduplicated-response/race", request, key, 201));

        MartenDeduplicatedResponseEndpoints.RaceReached(request.Name).ShouldBe(2);

        (await responses[1].ReadAsTextAsync()).ShouldBe(await responses[0].ReadAsTextAsync());
        responses[1].Context.Response.Headers.Location.ToString()
            .ShouldBe(responses[0].Context.Response.Headers.Location.ToString());

        // Only the winner keeps a header its own endpoint set: the loser's work rolled back, and so did its response
        responses.Count(x => x.Context.Response.Headers.ContainsKey(MartenDeduplicatedResponseEndpoints.AttemptHeader))
            .ShouldBe(1);

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await session.Query<DeduplicatedOrderDocument>().CountAsync(x => x.Name == request.Name,
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task a_concurrent_repeat_that_fails_on_its_own_work_first_is_still_answered_from_the_winner()
    {
        // The loser's append trips the stream's version before its commit reaches the claim
        var counterId = Guid.NewGuid();
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<DeduplicatedCounter>(counterId, new DeduplicatedCounterStarted());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = Guid.NewGuid().ToString();
        var command = new IncrementDeduplicatedCounter(counterId, "race-" + key);

        var responses = await Task.WhenAll(
            postAsync("/marten-deduplicated-response/counter-race", command, key, 200),
            postAsync("/marten-deduplicated-response/counter-race", command, key, 200));

        MartenDeduplicatedResponseEndpoints.RaceReached(command.Note).ShouldBe(2);
        (await responses[1].ReadAsTextAsync()).ShouldBe(await responses[0].ReadAsTextAsync());

        await using var query = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var events = await query.Events.FetchStreamAsync(counterId, token: TestContext.Current.CancellationToken);
        events.Count(x => x.Data is DeduplicatedCounterIncremented).ShouldBe(1);
    }

    [Fact]
    public async Task a_write_aggregate_of_the_aggregate_itself_claims_in_the_transaction()
    {
        // GH-????. Nothing in this signature names Marten: [WriteAggregate] binds the aggregate rather than its
        // IEventStream<T>, and the workflow applies the Marten transaction itself
        var counterId = Guid.NewGuid();
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<DeduplicatedCounter>(counterId, new DeduplicatedCounterStarted());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = Guid.NewGuid().ToString();
        var command = new IncrementDeduplicatedCounter(counterId, "aggregate-race-" + key);

        var responses = await Task.WhenAll(
            postAsync("/marten-deduplicated-response/aggregate-race", command, key, 200),
            postAsync("/marten-deduplicated-response/aggregate-race", command, key, 200));

        MartenDeduplicatedResponseEndpoints.RaceReached(command.Note).ShouldBe(2);
        (await responses[1].ReadAsTextAsync()).ShouldBe(await responses[0].ReadAsTextAsync());

        await using var query = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var events = await query.Events.FetchStreamAsync(counterId, token: TestContext.Current.CancellationToken);
        events.Count(x => x.Data is DeduplicatedCounterIncremented).ShouldBe(1);

        var source = sourceFor("/marten-deduplicated-response/aggregate-race");
        source.ShouldContain(".QueueClaim(");
        source.ShouldNotContain(".TryClaimAsync(");
        theLogs.Warnings.ShouldNotContain(x =>
            x.Contains("/marten-deduplicated-response/aggregate-race") && x.Contains("keeps claim-and-release"));
    }

    [Fact]
    public async Task an_unanswered_claim_from_claim_and_release_is_still_answered_from()
    {
        // A node still on claim-and-release, mid-request, during a rolling deploy
        var key = Guid.NewGuid().ToString();
        (await replayableStore().TryClaimAsync(storageIdFor(key), "a different request's fingerprint",
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        await postAsync("/marten-deduplicated-response/orders", new DeduplicatedOrder("unanswered-" + key), key, 422);
    }

    [Fact]
    public async Task a_commit_that_fails_with_no_claim_to_answer_from_is_rethrown_and_claims_nothing()
    {
        var key = Guid.NewGuid().ToString();

        await failingPostAsync("/marten-deduplicated-response/failing-commit",
            new DeduplicatedOrder("failing-commit-" + key), key);

        (await findClaimAsync(key)).ShouldBeNull();

        // On claim-and-release the release would also leave no claim; this is the transactional path
        sourceFor("/marten-deduplicated-response/failing-commit").ShouldContain(".QueueClaim(");
    }

    [Fact]
    public async Task an_unanswered_claim_for_the_same_request_is_refused_with_409()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("unanswered-same-" + key);

        await postAsync("/marten-deduplicated-response/orders", request, key, 201);
        var fingerprint = (await findClaimAsync(key)).ShouldNotBeNull().Fingerprint;

        // Swap the answered claim for an unanswered one with the same fingerprint, as a request still running on
        // claim-and-release would hold
        await replayableStore().DeleteExpiredAsync(DateTimeOffset.UtcNow.AddDays(30),
            TestContext.Current.CancellationToken);
        (await replayableStore().TryClaimAsync(storageIdFor(key), fingerprint, Guid.NewGuid().ToString(),
            DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken)).ShouldBeTrue();

        await postAsync("/marten-deduplicated-response/orders", request, key, 409);
        MartenDeduplicatedResponseEndpoints.Runs(request.Name).ShouldBe(1);
    }

    [Fact]
    public async Task a_failure_answer_claims_nothing_and_its_retry_runs()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("missing-" + key);

        await postAsync("/marten-deduplicated-response/missing", request, key, 404);
        (await findClaimAsync(key)).ShouldBeNull();

        await postAsync("/marten-deduplicated-response/missing", request, key, 404);
        MartenDeduplicatedResponseEndpoints.Runs(request.Name).ShouldBe(2);
    }

    [Fact]
    public async Task a_request_without_an_optional_key_runs_and_claims_nothing()
    {
        var request = new DeduplicatedOrder("no-key-" + Guid.NewGuid());

        for (var i = 0; i < 2; i++)
        {
            await theHost.Scenario(x =>
            {
                x.Post.Json(request).ToUrl("/marten-deduplicated-response/optional-key");
                x.StatusCodeShouldBe(201);
            });
        }

        MartenDeduplicatedResponseEndpoints.Runs(request.Name).ShouldBe(2);
        sourceFor("/marten-deduplicated-response/optional-key").ShouldContain(".QueueClaim(");
    }

    [Fact]
    public async Task without_claim_in_transaction_a_marten_endpoint_keeps_claim_and_release()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("default-" + key);

        await postAsync("/marten-deduplicated-response/orders-default", request, key, 201);
        await postAsync("/marten-deduplicated-response/orders-default", request, key, 201);
        MartenDeduplicatedResponseEndpoints.Runs(request.Name).ShouldBe(1);

        var source = sourceFor("/marten-deduplicated-response/orders-default");
        source.ShouldContain(".TryClaimAsync(");
        source.ShouldContain(".RecordResponseAsync(");
        source.ShouldNotContain(".QueueClaim(");
    }

    [Fact]
    public async Task a_response_produced_after_the_commit_keeps_claim_and_release()
    {
        // UpdatedAggregate fetches the aggregate after the commit; claiming before it would read it first
        var counterId = Guid.NewGuid();
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<DeduplicatedCounter>(counterId, new DeduplicatedCounterStarted());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = Guid.NewGuid().ToString();
        var command = new IncrementDeduplicatedCounter(counterId, "updated");

        var first = await postAsync("/marten-deduplicated-response/updated-aggregate", command, key, 200);
        var repeat = await postAsync("/marten-deduplicated-response/updated-aggregate", command, key, 200);

        (await first.ReadAsJsonAsync<DeduplicatedCounter>()).ShouldNotBeNull().Count.ShouldBe(1);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        var source = sourceFor("/marten-deduplicated-response/updated-aggregate");
        source.ShouldContain(".TryClaimAsync(");
        source.ShouldNotContain(".QueueClaim(");
    }

    [Fact]
    public async Task a_failure_after_the_commit_is_rethrown_and_its_unanswered_claim_refuses_repeats()
    {
        // The response cannot be serialized: the claim commits unanswered, then writing the response throws
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("unserializable-" + key);

        await failingPostAsync("/marten-deduplicated-response/unserializable", request, key);

        (await findClaimAsync(key)).ShouldNotBeNull().Response.ShouldBeNull();
        await postAsync("/marten-deduplicated-response/unserializable", request, key, 409);

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await session.Query<DeduplicatedOrderDocument>().CountAsync(x => x.Name == request.Name,
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task a_failure_inside_the_commit_after_the_database_commit_is_rethrown_and_its_retry_replays()
    {
        // On Marten the outbox flush and session listeners run inside SaveChangesAsync, after the database commit,
        // so this request's own claim is already committed when it fails: not a lost race to answer as a success
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("after-commit-" + key);

        await failingPostAsync("/marten-deduplicated-response/after-commit-failure", request, key);

        (await findClaimAsync(key)).ShouldNotBeNull().Response.ShouldNotBeNull().StatusCode.ShouldBe(201);

        await postAsync("/marten-deduplicated-response/after-commit-failure", request, key, 201);
        MartenDeduplicatedResponseEndpoints.Runs(request.Name).ShouldBe(1);

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await session.Query<DeduplicatedOrderDocument>().CountAsync(x => x.Name == request.Name,
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task an_endpoint_that_commits_nothing_keeps_claim_and_release()
    {
        await postAsync("/marten-deduplicated-response/tenant", new DeduplicatedOrder("warm"),
            Guid.NewGuid().ToString(), 201, tenant: "red");

        var source = sourceFor("/marten-deduplicated-response/tenant");
        source.ShouldContain(".TryClaimAsync(");
        source.ShouldContain(".RecordResponseAsync(");
        source.ShouldNotContain(".QueueClaim(");

        // No commit to hear about
        source.ShouldNotContain(nameof(HttpHandler.KeepDeduplicatedClaimIfCommitted));
    }

    [Fact]
    public async Task on_claim_and_release_a_failure_inside_the_commit_after_the_database_commit_keeps_the_claim()
    {
        // GH-5165. The work commits, then a session listener throws inside SaveChangesAsync. The claim used to be
        // released, because the response was never recorded, so the retry ran the endpoint a second time
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("after-commit-default-" + key);

        await failingPostAsync("/marten-deduplicated-response/after-commit-failure-default", request, key);

        // Kept, unanswered: the work is done, but there is no response to replay
        (await findClaimAsync(key)).ShouldNotBeNull().Response.ShouldBeNull();

        await postAsync("/marten-deduplicated-response/after-commit-failure-default", request, key, 409);
        MartenDeduplicatedResponseEndpoints.Runs(request.Name).ShouldBe(1);

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await session.Query<DeduplicatedOrderDocument>().CountAsync(x => x.Name == request.Name,
            TestContext.Current.CancellationToken)).ShouldBe(1);

        sourceFor("/marten-deduplicated-response/after-commit-failure-default")
            .ShouldContain(nameof(HttpHandler.KeepDeduplicatedClaimIfCommitted));
    }

    [Fact]
    public async Task on_claim_and_release_a_failed_commit_still_releases_the_claim()
    {
        // GH-5165, the other half: a commit that fails in the database rolled the work back, so the claim goes back
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("failing-commit-default-" + key);

        await failingPostAsync("/marten-deduplicated-response/failing-commit-default", request, key);

        (await findClaimAsync(key)).ShouldBeNull();

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await session.Query<DeduplicatedOrderDocument>().CountAsync(x => x.Name == request.Name,
            TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task the_fingerprint_still_reads_a_body_an_audited_member_has_already_read()
    {
        var key = Guid.NewGuid().ToString();

        await postAsync("/marten-deduplicated-response/audited", new AuditedDeduplicatedOrder("audited", "a"), key, 201);
        await postAsync("/marten-deduplicated-response/audited", new AuditedDeduplicatedOrder("audited", "a"), key, 201);
        await postAsync("/marten-deduplicated-response/audited", new AuditedDeduplicatedOrder("audited", "b"), key, 422);

        var source = sourceFor("/marten-deduplicated-response/audited");
        source.IndexOf("EnableBuffering", StringComparison.Ordinal)
            .ShouldBeLessThan(source.IndexOf("ReadJsonAsync", StringComparison.Ordinal), source);
    }

    [Fact]
    public async Task the_fingerprint_still_reads_a_body_the_aggregate_id_was_inferred_from()
    {
        var counterId = Guid.NewGuid();
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<DeduplicatedCounter>(counterId, new DeduplicatedCounterStarted());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = Guid.NewGuid().ToString();
        var command = new IncrementDeduplicatedCounter(counterId, "once");

        var first = await postAsync("/marten-deduplicated-response/counters", command, key, 200);
        var repeat = await postAsync("/marten-deduplicated-response/counters", command, key, 200);
        await postAsync("/marten-deduplicated-response/counters", command with { Note = "twice" }, key, 422);

        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        await using var query = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var events = await query.Events.FetchStreamAsync(counterId, token: TestContext.Current.CancellationToken);
        events.Count(x => x.Data is DeduplicatedCounterIncremented).ShouldBe(1);

        var source = sourceFor("/marten-deduplicated-response/counters");
        source.IndexOf("EnableBuffering", StringComparison.Ordinal)
            .ShouldBeLessThan(source.IndexOf("ReadJsonAsync", StringComparison.Ordinal), source);
    }

    [Fact]
    public async Task a_tenant_scope_isolates_tenants()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("per-tenant-" + key);

        var red = await postAsync("/marten-deduplicated-response/tenant", request, key, 201, tenant: "red");
        var blue = await postAsync("/marten-deduplicated-response/tenant", request, key, 201, tenant: "blue");
        var redAgain = await postAsync("/marten-deduplicated-response/tenant", request, key, 201, tenant: "red");

        (await blue.ReadAsTextAsync()).ShouldNotBe(await red.ReadAsTextAsync());
        (await redAgain.ReadAsTextAsync()).ShouldBe(await red.ReadAsTextAsync());
    }

    private IReplayableDeduplicationStore replayableStore()
        => (IReplayableDeduplicationStore)theHost.Services.GetRequiredService<IWolverineRuntime>().Storage;

    // DeduplicationScope.User, for an anonymous caller
    private static string storageIdFor(string key)
        => DeduplicatedResponses.StorageIdFor(
            HttpHandler.ScopeDeduplicationId(new DefaultHttpContext(), key, DeduplicationScope.User, null)!);

    private Task<DeduplicatedResponseClaim?> findClaimAsync(string key)
        => replayableStore().FindAsync(storageIdFor(key), TestContext.Current.CancellationToken);

    private string sourceFor(string route)
        => theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == route).SourceCode.ShouldNotBeNull();

    // As deduplicated_with_response.failingPostAsync: a 500 under the developer exception page, else the throw
    private async Task failingPostAsync<T>(string url, T body, string key) where T : class
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
        catch (Exception e) when (e is InvalidOperationException or global::Marten.Exceptions.MartenCommandException)
        {
            // Propagated straight through the test server
        }
    }

    private Task<IScenarioResult> postAsync<T>(string url, T body, string key, int status, string? tenant = null)
        where T : class
        => theHost.Scenario(x =>
        {
            x.Post.Json(body).ToUrl(url);
            x.WithRequestHeader("Idempotency-Key", key);
            if (tenant != null) x.WithRequestHeader("tenant", tenant);
            x.StatusCodeShouldBe(status);
        });
}

public class DeduplicatedOrderDocument
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public record AuditedDeduplicatedOrder([property: Audit] string Name, string Detail);

public record DeduplicatedCounterStarted;

public record DeduplicatedCounterIncremented(string Note);

public class DeduplicatedCounter
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(DeduplicatedCounterStarted _)
    {
    }

    public void Apply(DeduplicatedCounterIncremented _) => Count++;
}

// The aggregate id is inferred from the body, which audits it.
public record IncrementDeduplicatedCounter(Guid DeduplicatedCounterId, string Note);

public record DeduplicatedCounterAnswer(int Count);

public class FailAfterCommitMarker
{
    public Guid Id { get; set; }
}

// Throws from AfterCommitAsync, which Marten runs inside SaveChangesAsync once the database has committed, for any
// commit that wrote a FailAfterCommitMarker. Store() is an upsert, so the marker shows up as updated
public class FailAfterCommitListener : DocumentSessionListenerBase
{
    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        if (commit.Inserted.Concat(commit.Updated).OfType<FailAfterCommitMarker>().Any())
        {
            throw new InvalidOperationException("A session listener failed after the database commit");
        }

        return Task.CompletedTask;
    }
}

public class UnserializableDeduplicatedAnswer
{
    public string Value => throw new InvalidOperationException("This response cannot be serialized");
}

public static class MartenDeduplicatedResponseEndpoints
{
    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/marten-deduplicated-response/orders")]
    public static DeduplicatedOrderCreated PostOrder(DeduplicatedOrder request, IDocumentSession session)
    {
        _runs.AddOrUpdate(request.Name, 1, (_, count) => count + 1);
        var document = new DeduplicatedOrderDocument { Id = Guid.NewGuid(), Name = request.Name };
        session.Store(document);
        return new DeduplicatedOrderCreated(document.Id);
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/marten-deduplicated-response/audited")]
    public static DeduplicatedOrderCreated PostAudited(AuditedDeduplicatedOrder request, IDocumentSession session)
    {
        var document = new DeduplicatedOrderDocument { Id = Guid.NewGuid(), Name = request.Name };
        session.Store(document);
        return new DeduplicatedOrderCreated(document.Id);
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/marten-deduplicated-response/counters")]
    public static DeduplicatedCounterAnswer PostCounter(IncrementDeduplicatedCounter command,
        [WriteAggregate] IEventStream<DeduplicatedCounter> stream)
    {
        stream.AppendOne(new DeduplicatedCounterIncremented(command.Note));
        return new DeduplicatedCounterAnswer(stream.Aggregate!.Count + 1);
    }

    private static readonly ConcurrentDictionary<string, int> _runs = new();
    private static readonly ConcurrentDictionary<string, bool> _claimSeen = new();
    private static readonly ConcurrentDictionary<string, bool> _failed = new();
    private static readonly ConcurrentDictionary<string, int> _raceReached = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _raceBarriers = new();

    public const string AttemptHeader = "X-Deduplication-Attempt";

    public static bool ClaimSeenWhileRunning(string key) => _claimSeen[key];

    public static int RaceReached(string name) => _raceReached.GetValueOrDefault(name);

    // How many times the order work ran for a name: a repeat answered up front never reaches it
    public static int Runs(string name) => _runs.GetValueOrDefault(name);

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/marten-deduplicated-response/observe")]
    public static async Task<DeduplicatedOrderCreated> PostObserved(DeduplicatedOrder request,
        IDocumentSession session, HttpContext context, IWolverineRuntime runtime)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var scoped = HttpHandler.ScopeDeduplicationId(context, key, DeduplicationScope.User, null)!;
        var claim = await ((IReplayableDeduplicationStore)runtime.Storage).FindAsync(
            DeduplicatedResponses.StorageIdFor(scoped));
        _claimSeen[key] = claim != null;

        return PostOrder(request, session);
    }

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/marten-deduplicated-response/fail-once")]
    public static DeduplicatedOrderCreated PostFailOnce(DeduplicatedOrder request, IDocumentSession session)
    {
        var created = PostOrder(request, session);

        // Thrown after the work is queued, before the commit
        if (_failed.TryAdd(request.Name, true)) throw new InvalidOperationException("The first attempt fails");

        return created;
    }

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/marten-deduplicated-response/race")]
    public static async Task<DeduplicatedOrderCreated> PostRace(DeduplicatedOrder request,
        IDocumentSession session, HttpContext context)
    {
        await bothArriveAsync(request.Name);

        context.Response.Headers[AttemptHeader] = Guid.NewGuid().ToString();
        return PostOrder(request, session);
    }

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/marten-deduplicated-response/failing-commit")]
    public static DeduplicatedOrderCreated PostFailingCommit(DeduplicatedOrder request, IDocumentSession session)
    {
        var created = PostOrder(request, session);

        // Fails inside SaveChangesAsync, with no concurrent request to have committed a claim
        session.QueueSqlCommand("select 1 / 0");

        return created;
    }

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/marten-deduplicated-response/unserializable")]
    public static UnserializableDeduplicatedAnswer PostUnserializable(DeduplicatedOrder request,
        IDocumentSession session)
    {
        PostOrder(request, session);
        return new UnserializableDeduplicatedAnswer();
    }

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/marten-deduplicated-response/counter-race")]
    public static async Task<DeduplicatedCounterAnswer> PostCounterRace(IncrementDeduplicatedCounter command,
        [WriteAggregate] IEventStream<DeduplicatedCounter> stream)
    {
        // Both have loaded the same version before either appends
        await bothArriveAsync(command.Note);

        return PostCounter(command, stream);
    }

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/marten-deduplicated-response/aggregate-race")]
    public static async Task<(DeduplicatedCounterAnswer, Events)> PostAggregateRace(
        IncrementDeduplicatedCounter command, [WriteAggregate] DeduplicatedCounter counter)
    {
        await bothArriveAsync(command.Note);

        return (new DeduplicatedCounterAnswer(counter.Count + 1), [new DeduplicatedCounterIncremented(command.Note)]);
    }

    private static async Task bothArriveAsync(string name)
    {
        var barrier = _raceBarriers.GetOrAdd(name,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        if (_raceReached.AddOrUpdate(name, 1, (_, count) => count + 1) == 2) barrier.TrySetResult();

        // Bounded, so a regression fails the assertions rather than hanging the test
        await Task.WhenAny(barrier.Task, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/marten-deduplicated-response/after-commit-failure")]
    public static DeduplicatedOrderCreated PostAfterCommitFailure(DeduplicatedOrder request,
        IDocumentSession session)
    {
        var created = PostOrder(request, session);
        session.Store(new FailAfterCommitMarker { Id = Guid.NewGuid() });
        return created;
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/marten-deduplicated-response/after-commit-failure-default")]
    public static DeduplicatedOrderCreated PostAfterCommitFailureByDefault(DeduplicatedOrder request,
        IDocumentSession session)
        => PostAfterCommitFailure(request, session);

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/marten-deduplicated-response/failing-commit-default")]
    public static DeduplicatedOrderCreated PostFailingCommitByDefault(DeduplicatedOrder request,
        IDocumentSession session)
        => PostFailingCommit(request, session);

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/marten-deduplicated-response/orders-default")]
    public static DeduplicatedOrderCreated PostOrderByDefault(DeduplicatedOrder request, IDocumentSession session)
        => PostOrder(request, session);

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/marten-deduplicated-response/missing")]
    public static DeduplicatedOrderCreated? PostMissing(DeduplicatedOrder request, IDocumentSession session)
    {
        // The work commits, but the answer is a 404: nothing is claimed, so a retry runs again
        PostOrder(request, session);
        return null;
    }

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true, Required = false)]
    [WolverinePost("/marten-deduplicated-response/optional-key")]
    public static DeduplicatedOrderCreated PostOptionalKey(DeduplicatedOrder request, IDocumentSession session)
        => PostOrder(request, session);

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/marten-deduplicated-response/updated-aggregate")]
    public static (UpdatedAggregate, Events) PostUpdatedAggregate(IncrementDeduplicatedCounter command,
        [WriteAggregate] IEventStream<DeduplicatedCounter> stream)
        => (new UpdatedAggregate(), [new DeduplicatedCounterIncremented(command.Note)]);

    [DeduplicatedWithResponse(DeduplicationScope.Tenant | DeduplicationScope.User)]
    [WolverinePost("/marten-deduplicated-response/tenant")]
    public static DeduplicatedOrderCreated PostTenant(DeduplicatedOrder request) => new(Guid.NewGuid());
}
