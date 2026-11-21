using System.Security.Claims;
using Alba;
using IntegrationTests;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Marten.Events.Projections;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

public class updated_aggregate_claim_in_transaction : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await BeforeCommitResponseHost.StartAsync("http_before_commit_response",
            typeof(BeforeCommitResponseEndpoints), _ => { });
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Theory]
    [InlineData("/before-commit/bump")]
    [InlineData("/before-commit/bump-default")]
    public void the_response_is_projected_and_claimed_before_the_commit(string route)
    {
        deduplicated_with_response_claim_in_transaction_default.compile(theHost, route);

        var source = BeforeCommitResponseHost.SourceFor(theHost, route);

        var after = source.IndexOf("BeforeCommitResponseEndpoints.After(", StringComparison.Ordinal);
        var stamp = source.IndexOf("PendingEventMetadata.Stamp(documentSession)", StringComparison.Ordinal);
        var project = source.IndexOf(".ProjectLatest<", StringComparison.Ordinal);
        var claim = source.IndexOf(".QueueClaim(", StringComparison.Ordinal);
        var commit = source.IndexOf("documentSession.SaveChangesAsync(", StringComparison.Ordinal);

        after.ShouldBeGreaterThan(-1, source);
        stamp.ShouldBeGreaterThan(-1, source);
        stamp.ShouldBeLessThan(project, source);
        project.ShouldBeLessThan(claim, source);
        claim.ShouldBeLessThan(commit, source);
        after.ShouldBeLessThan(commit, source);

        source.ShouldNotContain(".FetchLatest<");
        source.ShouldNotContain(".TryClaimAsync(");
    }

    [Theory]
    [InlineData("/before-commit/not-deduplicated")]
    [InlineData("/before-commit/claim-and-release")]
    public void an_endpoint_that_does_not_claim_in_its_transaction_fetches_the_aggregate_after_the_commit(string route)
    {
        deduplicated_with_response_claim_in_transaction_default.compile(theHost, route);

        var source = BeforeCommitResponseHost.SourceFor(theHost, route);
        var commit = source.IndexOf("documentSession.SaveChangesAsync(", StringComparison.Ordinal);
        var fetch = source.IndexOf(".FetchLatest<", StringComparison.Ordinal);

        commit.ShouldBeGreaterThan(-1, source);
        fetch.ShouldBeGreaterThan(commit, source);
        source.ShouldNotContain("PendingEventMetadata");
        source.ShouldNotContain(".ProjectLatest<");
    }

    [Fact]
    public async Task a_repeat_answers_with_the_first_response_which_includes_its_own_events()
    {
        var id = await startCounterAsync();
        var key = Guid.NewGuid().ToString();

        var first = await postAsync("/before-commit/bump", new BumpStampedCounter(id), key);
        var repeat = await postAsync("/before-commit/bump", new BumpStampedCounter(id), key);

        var body = await first.ReadAsTextAsync();
        (await repeat.ReadAsTextAsync()).ShouldBe(body);

        var counter = (await first.ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();
        counter.Count.ShouldBe(3);
        counter.Version.ShouldBe(4);

        await using var session = store().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(4);
    }

    [Fact]
    public async Task the_response_carries_the_metadata_the_commit_writes()
    {
        var id = await startCounterAsync();

        var response = await postAsync("/before-commit/bump", new BumpStampedCounter(id), Guid.NewGuid().ToString());
        var answer = (await response.ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();

        await using var session = store().LightweightSession();
        var stored = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
        var latest = await session.Events.FetchLatest<StampedCounter>(id, TestContext.Current.CancellationToken);
        latest.ShouldNotBeNull();

        var appended = stored.Skip(2).ToArray();
        appended.Length.ShouldBe(2);

        appended[1].Timestamp.ShouldBe(appended[0].Timestamp);

        answer.LastBumped.ShouldBe(appended[1].Timestamp);
        answer.LastUserName.ShouldBe("han@rebellion.org");
        answer.LastCorrelationId.ShouldNotBeNullOrEmpty();
        answer.LastCorrelationId.ShouldBe(appended[1].CorrelationId);
        // An HTTP request has no causation id; pending_event_metadata_stamp covers one
        answer.LastCausationId.ShouldBe(appended[1].CausationId);
        answer.Version.ShouldBe(appended[1].Version);

        latest.LastBumped.ShouldBe(answer.LastBumped);
        latest.LastUserName.ShouldBe(answer.LastUserName);
        latest.LastCorrelationId.ShouldBe(answer.LastCorrelationId);
        latest.LastCausationId.ShouldBe(answer.LastCausationId);
        latest.Version.ShouldBe(answer.Version);
    }

    [Fact]
    public async Task a_request_that_appends_nothing_responds_with_the_stored_aggregate()
    {
        var id = await startCounterAsync();
        var key = Guid.NewGuid().ToString();

        var first = await postAsync("/before-commit/bump-nothing", new BumpStampedCounter(id), key);
        var repeat = await postAsync("/before-commit/bump-nothing", new BumpStampedCounter(id), key);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        var answer = (await first.ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();
        answer.Id.ShouldBe(id);
        answer.Count.ShouldBe(1);
        answer.Version.ShouldBe(2);
        await BeforeCommitResponseHost.ShouldMatchAFreshReadAsync(theHost, answer);
    }

    [Fact]
    public async Task responds_with_a_stream_started_in_the_same_request()
    {
        var id = Guid.NewGuid();

        var response = await postAsync("/before-commit/open", new OpenStampedCounter(id), Guid.NewGuid().ToString());
        var answer = (await response.ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();

        answer.Id.ShouldBe(id);
        answer.Count.ShouldBe(1);
        answer.Version.ShouldBe(2);
        await BeforeCommitResponseHost.ShouldMatchAFreshReadAsync(theHost, answer);
    }

    [Fact]
    public async Task the_generic_marker_responds_with_the_aggregate_it_names()
    {
        var counterId = await startCounterAsync();
        var ledgerId = Guid.NewGuid();
        await using (var session = store().LightweightSession())
        {
            session.Events.StartStream<StampedLedger>(ledgerId, new StampedLedgerOpened());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await postAsync("/before-commit/ledger", new PostToStampedLedger(counterId, ledgerId, 25),
            Guid.NewGuid().ToString());
        var ledger = (await response.ReadAsJsonAsync<StampedLedger>()).ShouldNotBeNull();

        ledger.Id.ShouldBe(ledgerId);
        ledger.Balance.ShouldBe(25);
        ledger.Version.ShouldBe(2);

        BeforeCommitResponseHost.SourceFor(theHost, "/before-commit/ledger").ShouldContain(".ProjectLatest<");
    }

    [Fact]
    public async Task resolves_a_strong_typed_identifier()
    {
        var id = Guid.NewGuid();
        await using (var session = store().LightweightSession())
        {
            session.Events.StartStream<KeyedCounter>(id, new StampedCounterStarted(), new StampedCounterBumped());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await postAsync("/before-commit/keyed", new BumpKeyedCounter(new KeyedCounterKey(id)),
            Guid.NewGuid().ToString());
        var answer = (await response.ReadAsJsonAsync<KeyedCounter>()).ShouldNotBeNull();

        answer.Id.ShouldBe(new KeyedCounterKey(id));
        answer.Count.ShouldBe(2);
        answer.Version.ShouldBe(3);

        BeforeCommitResponseHost.SourceFor(theHost, "/before-commit/keyed").ShouldContain(".ProjectLatest<");
    }

    [Fact]
    public async Task a_custom_marker_answers_from_this_transactions_events_before_the_commit()
    {
        var id = await startCounterAsync();

        var response = await postAsync("/before-commit/summary", new BumpStampedCounter(id), Guid.NewGuid().ToString());
        var summary = (await response.ReadAsJsonAsync<StampedCounterSummary>()).ShouldNotBeNull();

        await using var session = store().LightweightSession();
        var stored = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        summary.Count.ShouldBe(3);
        summary.Version.ShouldBe(4);
        summary.LastBumped.ShouldBe(stored.Last().Timestamp);

        var source = BeforeCommitResponseHost.SourceFor(theHost, "/before-commit/summary");
        var summarize = source.IndexOf("StampedCounterSummaryResponse.SummarizeAsync(", StringComparison.Ordinal);
        summarize.ShouldBeGreaterThan(-1, source);
        summarize.ShouldBeLessThan(source.IndexOf(".QueueClaim(", StringComparison.Ordinal), source);
    }

    [Fact]
    public async Task a_custom_marker_on_an_endpoint_whose_commit_comes_from_a_returned_marten_op()
    {
        var id = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        var first = await postAsync("/before-commit/start", new StartStampedCounter(id), key);
        var repeat = await postAsync("/before-commit/start", new StartStampedCounter(id), key);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        var answer = (await first.ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();
        answer.Id.ShouldBe(id);
        answer.Count.ShouldBe(1);
        answer.Version.ShouldBe(2);
        await BeforeCommitResponseHost.ShouldMatchAFreshReadAsync(theHost, answer);

        var source = BeforeCommitResponseHost.SourceFor(theHost, "/before-commit/start");
        var respond = source.IndexOf("StartedStampedCounterResponse.RespondAsync(", StringComparison.Ordinal);
        var claim = source.IndexOf(".QueueClaim(", StringComparison.Ordinal);
        var commit = source.IndexOf("documentSession.SaveChangesAsync(", StringComparison.Ordinal);

        respond.ShouldBeGreaterThan(-1, source);
        respond.ShouldBeLessThan(claim, source);
        claim.ShouldBeLessThan(commit, source);
    }

    private IDocumentStore store() => theHost.Services.GetRequiredService<IDocumentStore>();

    private async Task<Guid> startCounterAsync()
    {
        var id = Guid.NewGuid();
        await using var session = store().LightweightSession();
        session.Events.StartStream<StampedCounter>(id, new StampedCounterStarted(), new StampedCounterBumped());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private Task<IScenarioResult> postAsync<T>(string url, T body, string key) where T : class
        => BeforeCommitResponseHost.PostAsync(theHost, url, body, key);
}

public class updated_aggregate_claim_in_transaction_lifecycles
{
    public static TheoryData<ProjectionLifecycle, bool> Lifecycles => new()
    {
        { ProjectionLifecycle.Live, false },
        { ProjectionLifecycle.Live, true },
        { ProjectionLifecycle.Inline, false },
        { ProjectionLifecycle.Inline, true }
    };

    [Theory]
    [MemberData(nameof(Lifecycles))]
    public async Task responds_with_the_aggregate_the_commit_writes(ProjectionLifecycle lifecycle, bool identityMap)
    {
        await using var host = await BeforeCommitResponseHost.StartAsync(
            $"http_before_commit_{lifecycle}_{identityMap}".ToLowerInvariant(), typeof(BeforeCommitResponseEndpoints),
            m =>
            {
                m.Events.UseIdentityMapForAggregates = identityMap;

                if (lifecycle == ProjectionLifecycle.Live)
                {
                    m.Projections.LiveStreamAggregation<StampedCounter>();
                }
                else
                {
                    m.Projections.Snapshot<StampedCounter>(SnapshotLifecycle.Inline);
                }
            });

        var existing = Guid.NewGuid();
        await using (var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<StampedCounter>(existing, new StampedCounterStarted(), new StampedCounterBumped());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var bumped = (await (await BeforeCommitResponseHost.PostAsync(host, "/before-commit/bump",
                new BumpStampedCounter(existing), Guid.NewGuid().ToString()))
            .ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();

        bumped.Count.ShouldBe(3);
        bumped.Version.ShouldBe(4);
        await BeforeCommitResponseHost.ShouldMatchAFreshReadAsync(host, bumped);

        var started = Guid.NewGuid();
        var opened = (await (await BeforeCommitResponseHost.PostAsync(host, "/before-commit/open",
                new OpenStampedCounter(started), Guid.NewGuid().ToString()))
            .ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();

        opened.Id.ShouldBe(started);
        opened.Count.ShouldBe(1);
        opened.Version.ShouldBe(2);
        await BeforeCommitResponseHost.ShouldMatchAFreshReadAsync(host, opened);
    }
}

public class updated_aggregate_claim_in_transaction_on_string_streams : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await BeforeCommitResponseHost.StartAsync("http_before_commit_string",
            typeof(NamedCounterEndpoints), m => m.Events.StreamIdentity = StreamIdentity.AsString);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task responds_with_the_aggregate_built_before_the_commit()
    {
        var id = "counter-" + Guid.NewGuid();
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<NamedCounter>(id, new StampedCounterStarted(), new StampedCounterBumped());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = Guid.NewGuid().ToString();
        var first = await BeforeCommitResponseHost.PostAsync(theHost, "/before-commit/named", new BumpNamedCounter(id), key);
        var repeat = await BeforeCommitResponseHost.PostAsync(theHost, "/before-commit/named", new BumpNamedCounter(id), key);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        var answer = (await first.ReadAsJsonAsync<NamedCounter>()).ShouldNotBeNull();
        answer.Id.ShouldBe(id);
        answer.Count.ShouldBe(2);
        answer.Version.ShouldBe(3);

        var source = BeforeCommitResponseHost.SourceFor(theHost, "/before-commit/named");
        source.ShouldContain(".ProjectLatest<");
        source.ShouldContain(".QueueClaim(");
    }
}

public class updated_aggregate_claim_in_transaction_falls_back
{
    private static void configure(string store, StoreOptions m)
    {
        if (store == "quick append")
        {
            m.Events.AppendMode = EventAppendMode.Quick;
        }
        else
        {
            m.Projections.Snapshot<StampedCounter>(SnapshotLifecycle.Async);
        }
    }

    [Theory]
    [InlineData("quick append")]
    [InlineData("async snapshot")]
    public async Task to_claim_and_release(string store)
    {
        await using var host = await BeforeCommitResponseHost.StartAsync(
            $"http_before_commit_falls_back_{store.Replace(' ', '_')}", typeof(BeforeCommitResponseEndpoints),
            m => configure(store, m), throwWhenClaimInTransactionFallsBack: false);

        var id = Guid.NewGuid();
        await using (var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<StampedCounter>(id, new StampedCounterStarted(), new StampedCounterBumped());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = Guid.NewGuid().ToString();
        var first = await BeforeCommitResponseHost.PostAsync(host, "/before-commit/bump", new BumpStampedCounter(id), key);
        var repeat = await BeforeCommitResponseHost.PostAsync(host, "/before-commit/bump", new BumpStampedCounter(id), key);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        var answer = (await first.ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();
        answer.Count.ShouldBe(3);
        answer.Version.ShouldBe(4);

        var source = BeforeCommitResponseHost.SourceFor(host, "/before-commit/bump");
        var commit = source.IndexOf("documentSession.SaveChangesAsync(", StringComparison.Ordinal);
        commit.ShouldBeGreaterThan(-1, source);
        source.IndexOf(".FetchLatest<", StringComparison.Ordinal).ShouldBeGreaterThan(commit, source);
        source.ShouldContain(".TryClaimAsync(");
        source.ShouldNotContain(".QueueClaim(");
        source.ShouldNotContain(".ProjectLatest<");
        source.ShouldNotContain("PendingEventMetadata");
    }

    [Theory]
    [InlineData("quick append", "EventAppendMode.Quick")]
    [InlineData("async snapshot", "Async snapshot")]
    public async Task or_throws_when_the_fallback_is_refused(string store, string reason)
    {
        await using var host = await BeforeCommitResponseHost.StartAsync(
            $"http_before_commit_throws_{store.Replace(' ', '_')}", typeof(BeforeCommitResponseEndpoints),
            m => configure(store, m));

        var ex = Should.Throw<InvalidOperationException>(() =>
            deduplicated_with_response_claim_in_transaction_default.compile(host, "/before-commit/bump"));

        ex.Message.ShouldContain("/before-commit/bump");
        ex.Message.ShouldContain("ClaimInTransaction = true");
        ex.Message.ShouldContain(reason);
    }
}

public class updated_aggregate_claim_in_transaction_with_the_async_daemon
{
    [Fact]
    public async Task the_daemon_applies_each_event_to_the_snapshot_once()
    {
        await using var host = await BeforeCommitResponseHost.StartAsync("http_before_commit_daemon",
            typeof(BeforeCommitResponseEndpoints),
            m => m.Projections.Snapshot<StampedCounter>(SnapshotLifecycle.Async),
            throwWhenClaimInTransactionFallsBack: false);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        var caughtUp = await startCounterAsync(store);
        await waitForTheDaemonAsync(store, caughtUp);
        await bumpAsync(host, caughtUp);
        await waitForTheDaemonAsync(store, caughtUp);
        await snapshotShouldBeAsync(store, caughtUp, count: 3, version: 4);

        await daemon.StopAllAsync();
        var behind = await startCounterAsync(store);
        await bumpAsync(host, behind);
        await daemon.StartAllAsync();
        await waitForTheDaemonAsync(store, behind);
        await snapshotShouldBeAsync(store, behind, count: 3, version: 4);

        await bumpAsync(host, caughtUp);
        await bumpAsync(host, behind);
        await waitForTheDaemonAsync(store, caughtUp);
        await waitForTheDaemonAsync(store, behind);
        await snapshotShouldBeAsync(store, caughtUp, count: 5, version: 6);
        await snapshotShouldBeAsync(store, behind, count: 5, version: 6);
    }

    private static async Task<Guid> startCounterAsync(IDocumentStore store)
    {
        var id = Guid.NewGuid();
        await using var session = store.LightweightSession();
        session.Events.StartStream<StampedCounter>(id, new StampedCounterStarted(), new StampedCounterBumped());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private static Task bumpAsync(IAlbaHost host, Guid id)
        => BeforeCommitResponseHost.PostAsync(host, "/before-commit/bump", new BumpStampedCounter(id),
            Guid.NewGuid().ToString());

    private static async Task waitForTheDaemonAsync(IDocumentStore store, Guid id)
    {
        await using var session = store.LightweightSession();
        var last = (await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken)).Last().Sequence;

        var timeout = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var progress = await store.Advanced.AllProjectionProgress(token: TestContext.Current.CancellationToken);
            if (progress.Any(x => x.ShardName.Contains(nameof(StampedCounter)) && x.Sequence >= last)) return;

            if (DateTimeOffset.UtcNow > timeout)
            {
                throw new TimeoutException($"The daemon did not reach sequence {last}");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static async Task snapshotShouldBeAsync(IDocumentStore store, Guid id, int count, long version)
    {
        await using var session = store.LightweightSession();
        var snapshot = (await session.LoadAsync<StampedCounter>(id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();

        snapshot.Count.ShouldBe(count);
        snapshot.Version.ShouldBe(version);
    }
}

internal static class BeforeCommitResponseHost
{
    public static async Task<IAlbaHost> StartAsync(string schema, Type endpoints, Action<StoreOptions> configure,
        bool throwWhenClaimInTransactionFallsBack = true)
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.EnableDeduplicatedResponses = true;
            opts.Durability.DeduplicationWindow = 1.Hours();
            opts.EnableRelayOfUserName = true;

            // No AutoApplyTransactions(): an endpoint returning only an IMartenOp gets its commit from that op
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(BeforeCommitResponseHost).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = schema;
            m.DisableNpgsqlLogging = true;

            // Rich writes the timestamp assigned before the commit; plain Quick takes the database server's
            m.Events.AppendMode = EventAppendMode.Rich;
            m.Events.TimeProvider = new SteppingClock();
            m.Events.MetadataConfig.UserNameEnabled = true;
            m.Events.MetadataConfig.CorrelationIdEnabled = true;
            m.Events.MetadataConfig.CausationIdEnabled = true;

            configure(m);
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        var host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.DefaultClaimInTransaction = true;
            opts.ThrowWhenClaimInTransactionFallsBack = throwWhenClaimInTransactionFallsBack;

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a before-commit response test endpoint", type => type != endpoints));
        }));

        await ((IHost)host).ResetResourceState();
        return host;
    }

    public static Task<IScenarioResult> PostAsync<T>(IAlbaHost host, string url, T body, string key) where T : class
        => host.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "han@rebellion.org")], "TestAuth")));
            x.Post.Json(body).ToUrl(url);
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(200);
        });

    public static string SourceFor(IAlbaHost host, string route)
        => host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == route).SourceCode.ShouldNotBeNull();

    public static async Task ShouldMatchAFreshReadAsync(IAlbaHost host, StampedCounter answer)
    {
        await using var query = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var latest = await query.Events.FetchLatest<StampedCounter>(answer.Id, TestContext.Current.CancellationToken);

        latest.ShouldNotBeNull();
        latest.Count.ShouldBe(answer.Count);
        latest.Version.ShouldBe(answer.Version);
        latest.LastBumped.ShouldBe(answer.LastBumped);
    }
}

/// <summary>
/// Advances on every read, and never lands on a whole microsecond.
/// </summary>
public class SteppingClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).Ticks + 7;

    public override DateTimeOffset GetUtcNow()
        => new(Interlocked.Add(ref _ticks, 12_345_670), TimeSpan.Zero);
}

public record StampedCounterStarted;

public record StampedCounterBumped;

public class StampedCounter
{
    public Guid Id { get; set; }
    public long Version { get; set; }
    public int Count { get; set; }
    public DateTimeOffset LastBumped { get; set; }
    public string? LastUserName { get; set; }
    public string? LastCorrelationId { get; set; }
    public string? LastCausationId { get; set; }

    public static StampedCounter Create(StampedCounterStarted _) => new();

    public void Apply(IEvent<StampedCounterBumped> e)
    {
        Count++;
        LastBumped = e.Timestamp;
        LastUserName = e.UserName;
        LastCorrelationId = e.CorrelationId;
        LastCausationId = e.CausationId;
    }
}

public record StampedLedgerOpened;

public record StampedLedgerPosted(int Amount);

public class StampedLedger
{
    public Guid Id { get; set; }
    public long Version { get; set; }
    public int Balance { get; set; }

    public static StampedLedger Create(StampedLedgerOpened _) => new();

    public void Apply(StampedLedgerPosted e) => Balance += e.Amount;
}

public readonly record struct KeyedCounterKey(Guid Value);

public class KeyedCounter
{
    public KeyedCounterKey Id { get; set; }
    public long Version { get; set; }
    public int Count { get; set; }

    public static KeyedCounter Create(StampedCounterStarted _) => new();

    public void Apply(StampedCounterBumped _) => Count++;
}

public class NamedCounter
{
    public string Id { get; set; } = "";
    public long Version { get; set; }
    public int Count { get; set; }

    public static NamedCounter Create(StampedCounterStarted _) => new();

    public void Apply(StampedCounterBumped _) => Count++;
}

public record BumpStampedCounter(Guid StampedCounterId);

public record OpenStampedCounter(Guid StampedCounterId);

public record StartStampedCounter(Guid StampedCounterId);

public record PostToStampedLedger(Guid StampedCounterId, Guid StampedLedgerId, int Amount);

public record BumpKeyedCounter(KeyedCounterKey KeyedCounterId);

public record BumpNamedCounter(string NamedCounterId);

public record StampedCounterSummary(int Count, long Version, DateTimeOffset LastBumped);

#region sample_custom_before_commit_response

/// <summary>
/// Responds with a summary of the aggregate built from this transaction's events, before the commit.
/// </summary>
public class StampedCounterSummaryResponse : IResponseAware
{
    public static void ConfigureResponse(IChain chain)
    {
        chain.UseForResponseBeforeCommit(new MethodCall(typeof(StampedCounterSummaryResponse),
            nameof(SummarizeAsync)));
    }

    public static async Task<StampedCounterSummary> SummarizeAsync(BumpStampedCounter command,
        IDocumentSession session, CancellationToken cancellation)
    {
        PendingEventMetadata.Stamp(session);
        var counter = await session.Events.ProjectLatest<StampedCounter>(command.StampedCounterId, cancellation);
        return new StampedCounterSummary(counter!.Count, counter.Version, counter.LastBumped);
    }
}

#endregion

/// <summary>
/// Responds with the stream that a returned IStartStream starts, before the commit.
/// </summary>
public class StartedStampedCounterResponse : IResponseAware
{
    public static void ConfigureResponse(IChain chain)
    {
        chain.UseForResponseBeforeCommit(new MethodCall(typeof(StartedStampedCounterResponse), nameof(RespondAsync)));
    }

    public static async Task<StampedCounter> RespondAsync(StartStampedCounter command, IDocumentSession session,
        CancellationToken cancellation)
    {
        PendingEventMetadata.Stamp(session);
        return (await session.Events.ProjectLatest<StampedCounter>(command.StampedCounterId, cancellation))!;
    }
}

public static class BeforeCommitResponseEndpoints
{
    #region sample_updated_aggregate_with_claim_in_transaction

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/before-commit/bump")]
    public static (UpdatedAggregate, Events) Bump(BumpStampedCounter command,
        [WriteAggregate] StampedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped(), new StampedCounterBumped()]);

    #endregion

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/before-commit/bump-default")]
    public static (UpdatedAggregate, Events) BumpByDefault(BumpStampedCounter command,
        [WriteAggregate] StampedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped(), new StampedCounterBumped()]);

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/before-commit/bump-nothing")]
    public static (UpdatedAggregate, Events) BumpNothing(BumpStampedCounter command,
        [WriteAggregate] StampedCounter counter)
        => (new UpdatedAggregate(), []);

    [WolverinePost("/before-commit/not-deduplicated")]
    public static (UpdatedAggregate, Events) BumpWithoutDeduplication(BumpStampedCounter command,
        [WriteAggregate] StampedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped()]);

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = false)]
    [WolverinePost("/before-commit/claim-and-release")]
    public static (UpdatedAggregate, Events) BumpWithClaimAndRelease(BumpStampedCounter command,
        [WriteAggregate] StampedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped()]);

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/before-commit/open")]
    public static UpdatedAggregate Open(OpenStampedCounter command,
        [WriteAggregate(Required = false)] IEventStream<StampedCounter> stream)
    {
        stream.AppendOne(new StampedCounterStarted());
        stream.AppendOne(new StampedCounterBumped());
        return new UpdatedAggregate();
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/before-commit/ledger")]
    public static UpdatedAggregate<StampedLedger> PostToLedger(PostToStampedLedger command,
        [WriteAggregate] IEventStream<StampedCounter> counter,
        [WriteAggregate] IEventStream<StampedLedger> ledger)
    {
        counter.AppendOne(new StampedCounterBumped());
        ledger.AppendOne(new StampedLedgerPosted(command.Amount));
        return new UpdatedAggregate<StampedLedger>();
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/before-commit/keyed")]
    public static (UpdatedAggregate, Events) BumpKeyed(BumpKeyedCounter command,
        [WriteAggregate] KeyedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped()]);

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/before-commit/summary")]
    public static (StampedCounterSummaryResponse, Events) Summarize(BumpStampedCounter command,
        [WriteAggregate] StampedCounter counter)
        => (new StampedCounterSummaryResponse(), [new StampedCounterBumped(), new StampedCounterBumped()]);

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/before-commit/start")]
    public static (StartedStampedCounterResponse, IStartStream) Start(StartStampedCounter command)
        => (new StartedStampedCounterResponse(),
            MartenOps.StartStream<StampedCounter>(command.StampedCounterId, new StampedCounterStarted(),
                new StampedCounterBumped()));

    // Inserted at the head of every endpoint's postprocessors here, ahead of the response
    public static void After()
    {
    }
}

public static class NamedCounterEndpoints
{
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/before-commit/named")]
    public static (UpdatedAggregate, Events) Bump(BumpNamedCounter command, [WriteAggregate] NamedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped()]);
}
