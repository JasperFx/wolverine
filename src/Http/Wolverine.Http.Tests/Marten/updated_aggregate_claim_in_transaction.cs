using System.Collections.Concurrent;
using System.Security.Claims;
using Alba;
using IntegrationTests;
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
using Wolverine.Marten;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-5721. UpdatedAggregate built before the commit, so the claim rides the endpoint's transaction.
/// </summary>
public class updated_aggregate_claim_in_transaction : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await UpdatedAggregateClaimHost.StartAsync("http_updated_aggregate_claim",
            typeof(UpdatedAggregateClaimEndpoints), _ => { });
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Theory]
    [InlineData("/updated-aggregate-claim/bump")]
    [InlineData("/updated-aggregate-claim/bump-as-aggregate-handler")]
    public void a_claiming_endpoint_builds_its_response_before_the_commit(string route)
    {
        deduplicated_with_response_claim_in_transaction_default.compile(theHost, route);

        var source = UpdatedAggregateClaimHost.SourceFor(theHost, route);

        var watch = source.IndexOf("WatchForDeduplicatedRefusal(", StringComparison.Ordinal);
        var project = source.IndexOf(".ProjectLatestForCommit<", StringComparison.Ordinal);
        var claim = source.IndexOf(".QueueClaim(", StringComparison.Ordinal);
        var commit = source.IndexOf("documentSession.SaveChangesAsync(", StringComparison.Ordinal);

        // Under Rich, ProjectLatestForCommit can throw the commit's version conflict. Inside the watch, a concurrent
        // repeat that throws there is still answered from the winner's claim
        watch.ShouldBeGreaterThan(-1, source);
        watch.ShouldBeLessThan(project, source);
        project.ShouldBeLessThan(claim, source);
        claim.ShouldBeLessThan(commit, source);

        source.ShouldNotContain(".FetchLatest<");
        source.ShouldNotContain(".TryClaimAsync(");
    }

    [Theory]
    [InlineData("/updated-aggregate-claim/not-deduplicated")]
    [InlineData("/updated-aggregate-claim/claim-and-release")]
    public void an_endpoint_that_does_not_claim_in_its_transaction_fetches_the_aggregate_after_the_commit(string route)
    {
        deduplicated_with_response_claim_in_transaction_default.compile(theHost, route);

        var source = UpdatedAggregateClaimHost.SourceFor(theHost, route);
        var commit = source.IndexOf("documentSession.SaveChangesAsync(", StringComparison.Ordinal);

        commit.ShouldBeGreaterThan(-1, source);
        source.IndexOf(".FetchLatest<", StringComparison.Ordinal).ShouldBeGreaterThan(commit, source);
        source.ShouldNotContain(".ProjectLatestForCommit<");
    }

    [Fact]
    public async Task a_repeat_answers_with_the_first_response_which_includes_its_own_events()
    {
        var id = await startCounterAsync();
        var key = Guid.NewGuid().ToString();

        var first = await postAsync("/updated-aggregate-claim/bump", new BumpStampedCounter(id), key);
        var repeat = await postAsync("/updated-aggregate-claim/bump", new BumpStampedCounter(id), key);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        var counter = (await first.ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();
        counter.Count.ShouldBe(3);
        counter.Version.ShouldBe(4);

        await using var session = store().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(4);
    }

    [Fact]
    public async Task a_request_that_appends_nothing_responds_with_the_stored_aggregate()
    {
        var id = await startCounterAsync();
        var key = Guid.NewGuid().ToString();

        var first = await postAsync("/updated-aggregate-claim/bump-nothing", new BumpStampedCounter(id), key);
        var repeat = await postAsync("/updated-aggregate-claim/bump-nothing", new BumpStampedCounter(id), key);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        var answer = (await first.ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();
        answer.Id.ShouldBe(id);
        answer.Count.ShouldBe(1);
        answer.Version.ShouldBe(2);
        await UpdatedAggregateClaimHost.ShouldMatchAFreshReadAsync(theHost, answer);
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

        var response = await postAsync("/updated-aggregate-claim/ledger",
            new PostToStampedLedger(counterId, ledgerId, 25), Guid.NewGuid().ToString());
        var ledger = (await response.ReadAsJsonAsync<StampedLedger>()).ShouldNotBeNull();

        ledger.Id.ShouldBe(ledgerId);
        ledger.Balance.ShouldBe(25);
        ledger.Version.ShouldBe(2);
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

        var response = await postAsync("/updated-aggregate-claim/keyed", new BumpKeyedCounter(new KeyedCounterKey(id)),
            Guid.NewGuid().ToString());
        var answer = (await response.ReadAsJsonAsync<KeyedCounter>()).ShouldNotBeNull();

        answer.Id.ShouldBe(new KeyedCounterKey(id));
        answer.Count.ShouldBe(2);
        answer.Version.ShouldBe(3);
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
        => UpdatedAggregateClaimHost.PostAsync(theHost, url, body, key);
}

public class updated_aggregate_claim_in_transaction_lifecycles
{
    public static TheoryData<ProjectionLifecycle, bool> Lifecycles => new()
    {
        { ProjectionLifecycle.Live, false },
        { ProjectionLifecycle.Live, true },
        { ProjectionLifecycle.Inline, false },
        { ProjectionLifecycle.Inline, true },
        { ProjectionLifecycle.Async, false },
        { ProjectionLifecycle.Async, true }
    };

    [Theory]
    [MemberData(nameof(Lifecycles))]
    public async Task responds_with_the_aggregate_the_commit_writes(ProjectionLifecycle lifecycle, bool identityMap)
    {
        await using var host = await UpdatedAggregateClaimHost.StartAsync(
            $"http_updated_aggregate_claim_{lifecycle}_{identityMap}".ToLowerInvariant(),
            typeof(UpdatedAggregateClaimEndpoints), m =>
            {
                m.Events.UseIdentityMapForAggregates = identityMap;

                switch (lifecycle)
                {
                    case ProjectionLifecycle.Live:
                        m.Projections.LiveStreamAggregation<StampedCounter>();
                        break;
                    case ProjectionLifecycle.Inline:
                        m.Projections.Snapshot<StampedCounter>(SnapshotLifecycle.Inline);
                        break;
                    default:
                        m.Projections.Snapshot<StampedCounter>(SnapshotLifecycle.Async);
                        break;
                }
            });

        var existing = Guid.NewGuid();
        await using (var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<StampedCounter>(existing, new StampedCounterStarted(),
                new StampedCounterBumped());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var bumped = (await (await UpdatedAggregateClaimHost.PostAsync(host, "/updated-aggregate-claim/bump",
                new BumpStampedCounter(existing), Guid.NewGuid().ToString()))
            .ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();

        bumped.Count.ShouldBe(3);
        bumped.Version.ShouldBe(4);
        await UpdatedAggregateClaimHost.ShouldMatchAFreshReadAsync(host, bumped);

        var started = Guid.NewGuid();
        var opened = (await (await UpdatedAggregateClaimHost.PostAsync(host, "/updated-aggregate-claim/open",
                new OpenStampedCounter(started), Guid.NewGuid().ToString()))
            .ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();

        opened.Id.ShouldBe(started);
        opened.Count.ShouldBe(1);
        opened.Version.ShouldBe(2);
        await UpdatedAggregateClaimHost.ShouldMatchAFreshReadAsync(host, opened);
    }
}

public class updated_aggregate_claim_in_transaction_with_the_async_daemon
{
    [Fact]
    public async Task the_daemon_applies_each_event_to_the_snapshot_once()
    {
        await using var host = await UpdatedAggregateClaimHost.StartAsync("http_updated_aggregate_claim_daemon",
            typeof(UpdatedAggregateClaimEndpoints),
            m => m.Projections.Snapshot<StampedCounter>(SnapshotLifecycle.Async));

        var store = host.Services.GetRequiredService<IDocumentStore>();

        var id = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<StampedCounter>(id, new StampedCounterStarted(), new StampedCounterBumped());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await UpdatedAggregateClaimHost.PostAsync(host, "/updated-aggregate-claim/bump", new BumpStampedCounter(id),
            Guid.NewGuid().ToString());

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await waitForTheDaemonAsync(store, id);

        await using var query = store.LightweightSession();
        var snapshot = (await query.LoadAsync<StampedCounter>(id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();

        snapshot.Count.ShouldBe(3);
        snapshot.Version.ShouldBe(4);
    }

    private static async Task waitForTheDaemonAsync(IDocumentStore store, Guid id)
    {
        await using var session = store.LightweightSession();
        var last = (await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken)).Last()
            .Sequence;

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
}

public class updated_aggregate_claim_in_transaction_on_string_streams : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await UpdatedAggregateClaimHost.StartAsync("http_updated_aggregate_claim_string",
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
        var first = await UpdatedAggregateClaimHost.PostAsync(theHost, "/updated-aggregate-claim/named",
            new BumpNamedCounter(id), key);
        var repeat = await UpdatedAggregateClaimHost.PostAsync(theHost, "/updated-aggregate-claim/named",
            new BumpNamedCounter(id), key);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        var answer = (await first.ReadAsJsonAsync<NamedCounter>()).ShouldNotBeNull();
        answer.Id.ShouldBe(id);
        answer.Count.ShouldBe(2);
        answer.Version.ShouldBe(3);
    }
}

public class updated_aggregate_claim_in_transaction_on_quick_append
{
    [Fact]
    public async Task falls_back_to_claim_and_release()
    {
        await using var host = await UpdatedAggregateClaimHost.StartAsync("http_updated_aggregate_claim_quick",
            typeof(UpdatedAggregateClaimEndpoints), m => m.Events.AppendMode = EventAppendMode.Quick,
            throwWhenClaimInTransactionFallsBack: false);

        var id = Guid.NewGuid();
        await using (var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<StampedCounter>(id, new StampedCounterStarted(), new StampedCounterBumped());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = Guid.NewGuid().ToString();
        var first = await UpdatedAggregateClaimHost.PostAsync(host, "/updated-aggregate-claim/bump",
            new BumpStampedCounter(id), key);
        var repeat = await UpdatedAggregateClaimHost.PostAsync(host, "/updated-aggregate-claim/bump",
            new BumpStampedCounter(id), key);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        var answer = (await first.ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();
        answer.Count.ShouldBe(3);
        answer.Version.ShouldBe(4);
    }

    [Fact]
    public async Task or_throws_when_the_fallback_is_refused()
    {
        await using var host = await UpdatedAggregateClaimHost.StartAsync("http_updated_aggregate_claim_quick_throws",
            typeof(UpdatedAggregateClaimEndpoints), m => m.Events.AppendMode = EventAppendMode.Quick);

        var ex = Should.Throw<InvalidOperationException>(() =>
            deduplicated_with_response_claim_in_transaction_default.compile(host, "/updated-aggregate-claim/bump"));

        ex.Message.ShouldContain("/updated-aggregate-claim/bump");
        ex.Message.ShouldContain("only produced after the commit");
    }

    [Fact]
    public async Task but_not_with_server_timestamps()
    {
        await using var host = await UpdatedAggregateClaimHost.StartAsync(
            "http_updated_aggregate_claim_server_stamps", typeof(UpdatedAggregateClaimEndpoints),
            m => m.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps);

        var id = Guid.NewGuid();
        await using (var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<StampedCounter>(id, new StampedCounterStarted(), new StampedCounterBumped());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var answer = (await (await UpdatedAggregateClaimHost.PostAsync(host, "/updated-aggregate-claim/bump",
                new BumpStampedCounter(id), Guid.NewGuid().ToString()))
            .ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();

        answer.Count.ShouldBe(3);
    }
}

public class updated_aggregate_claim_in_transaction_on_rich_append : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await UpdatedAggregateClaimHost.StartAsync("http_updated_aggregate_claim_rich",
            typeof(UpdatedAggregateClaimEndpoints), m => m.Events.AppendMode = EventAppendMode.Rich);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task a_concurrent_repeat_is_answered_with_the_winners_response()
    {
        var id = Guid.NewGuid();
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<StampedCounter>(id, new StampedCounterStarted(), new StampedCounterBumped());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = Guid.NewGuid().ToString();
        var request = new RaceStampedCounter(id, Guid.NewGuid().ToString());

        var responses = await Task.WhenAll(
            UpdatedAggregateClaimHost.PostAsync(theHost, "/updated-aggregate-claim/race", request, key),
            UpdatedAggregateClaimHost.PostAsync(theHost, "/updated-aggregate-claim/race", request, key));

        (await responses[1].ReadAsTextAsync()).ShouldBe(await responses[0].ReadAsTextAsync());
        RaceArrivals.Arrivals(request.Name).ShouldBe(2);

        var answer = (await responses[0].ReadAsJsonAsync<StampedCounter>()).ShouldNotBeNull();
        answer.Count.ShouldBe(2);
        answer.Version.ShouldBe(3);

        await using var query = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        (await query.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken)).Count.ShouldBe(3);
    }
}

internal static class UpdatedAggregateClaimHost
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

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(UpdatedAggregateClaimHost).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = schema;
            m.DisableNpgsqlLogging = true;
            m.Events.TimeProvider = new SteppingClock();

            configure(m);
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        var host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.DefaultClaimInTransaction = true;
            opts.ThrowWhenClaimInTransactionFallsBack = throwWhenClaimInTransactionFallsBack;

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not an UpdatedAggregate claim test endpoint", type => type != endpoints));
        }));

        await ((IHost)host).ResetResourceState();
        return host;
    }

    public static Task<IScenarioResult> PostAsync<T>(IAlbaHost host, string url, T body, string key) where T : class
        => host.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "updated-aggregate-claim")], "TestAuth")));
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

public class SteppingClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).Ticks + 7;

    public override DateTimeOffset GetUtcNow()
        => new(Interlocked.Add(ref _ticks, 12_345_670), TimeSpan.Zero);
}

public static class RaceArrivals
{
    private static readonly ConcurrentDictionary<string, int> _arrivals = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _both = new();

    public static int Arrivals(string name) => _arrivals.GetValueOrDefault(name);

    public static async Task BothArriveAsync(string name)
    {
        var both = _both.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_arrivals.AddOrUpdate(name, 1, (_, count) => count + 1) == 2) both.TrySetResult();
        await Task.WhenAny(both.Task, Task.Delay(10.Seconds()));
    }
}

public record StampedCounterStarted;

public record StampedCounterBumped;

public class StampedCounter
{
    public Guid Id { get; set; }
    public long Version { get; set; }
    public int Count { get; set; }
    public DateTimeOffset LastBumped { get; set; }

    public static StampedCounter Create(StampedCounterStarted _) => new();

    public void Apply(IEvent<StampedCounterBumped> e)
    {
        Count++;
        LastBumped = e.Timestamp;
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

public record RaceStampedCounter(Guid StampedCounterId, string Name);

public record OpenStampedCounter(Guid StampedCounterId);

public record PostToStampedLedger(Guid StampedCounterId, Guid StampedLedgerId, int Amount);

public record BumpKeyedCounter(KeyedCounterKey KeyedCounterId);

public record BumpNamedCounter(string NamedCounterId);

public static class UpdatedAggregateClaimEndpoints
{
    #region sample_updated_aggregate_with_claim_in_transaction

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/updated-aggregate-claim/bump")]
    public static (UpdatedAggregate, Events) Bump(BumpStampedCounter command,
        [WriteAggregate] StampedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped(), new StampedCounterBumped()]);

    #endregion

    [AggregateHandler]
    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/updated-aggregate-claim/bump-as-aggregate-handler")]
    public static (UpdatedAggregate, Events) BumpAsAggregateHandler(BumpStampedCounter command,
        StampedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped()]);

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/updated-aggregate-claim/bump-nothing")]
    public static (UpdatedAggregate, Events) BumpNothing(BumpStampedCounter command,
        [WriteAggregate] StampedCounter counter)
        => (new UpdatedAggregate(), []);

    [WolverinePost("/updated-aggregate-claim/not-deduplicated")]
    public static (UpdatedAggregate, Events) BumpWithoutDeduplication(BumpStampedCounter command,
        [WriteAggregate] StampedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped()]);

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = false)]
    [WolverinePost("/updated-aggregate-claim/claim-and-release")]
    public static (UpdatedAggregate, Events) BumpWithClaimAndRelease(BumpStampedCounter command,
        [WriteAggregate] StampedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped()]);

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/updated-aggregate-claim/race")]
    public static async Task<(UpdatedAggregate, Events)> Race(RaceStampedCounter command,
        [WriteAggregate] StampedCounter counter)
    {
        await RaceArrivals.BothArriveAsync(command.Name);
        return (new UpdatedAggregate(), [new StampedCounterBumped()]);
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/updated-aggregate-claim/open")]
    public static UpdatedAggregate Open(OpenStampedCounter command,
        [WriteAggregate(Required = false)] IEventStream<StampedCounter> stream)
    {
        stream.AppendOne(new StampedCounterStarted());
        stream.AppendOne(new StampedCounterBumped());
        return new UpdatedAggregate();
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/updated-aggregate-claim/ledger")]
    public static UpdatedAggregate<StampedLedger> PostToLedger(PostToStampedLedger command,
        [WriteAggregate] IEventStream<StampedCounter> counter,
        [WriteAggregate] IEventStream<StampedLedger> ledger)
    {
        counter.AppendOne(new StampedCounterBumped());
        ledger.AppendOne(new StampedLedgerPosted(command.Amount));
        return new UpdatedAggregate<StampedLedger>();
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/updated-aggregate-claim/keyed")]
    public static (UpdatedAggregate, Events) BumpKeyed(BumpKeyedCounter command,
        [WriteAggregate] KeyedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped()]);
}

public static class NamedCounterEndpoints
{
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/updated-aggregate-claim/named")]
    public static (UpdatedAggregate, Events) Bump(BumpNamedCounter command, [WriteAggregate] NamedCounter counter)
        => (new UpdatedAggregate(), [new StampedCounterBumped()]);
}
