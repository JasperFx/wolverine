using Alba;
using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Marten;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-5721. A <c>[DeduplicatedWithResponse]</c> endpoint that returns <c>UpdatedAggregate</c> and claims in its
/// transaction builds the aggregate before the commit with Marten's <c>ProjectLatestForCommit</c>, and stores it with
/// the claim. The response is the aggregate as the commit writes it.
/// </summary>
public class updated_aggregate_claim_in_transaction : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.EnableDeduplicatedResponses = true;
            opts.Durability.DeduplicationWindow = 1.Hours();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Policies.AutoApplyTransactions();
            opts.Discovery.IncludeAssembly(typeof(updated_aggregate_claim_in_transaction).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "http_updated_aggregate_claim";
            m.DisableNpgsqlLogging = true;

            m.Projections.Snapshot<ClaimedInlineCounter>(SnapshotLifecycle.Inline);
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            // Nothing here may fall back to claim-and-release
            opts.ThrowWhenClaimInTransactionFallsBack = true;

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not an UpdatedAggregate claim test endpoint",
                    type => type != typeof(ClaimedCounterEndpoints)));
        }));

        await ((IHost)theHost).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    private async Task<Guid> startAsync<T>() where T : class
    {
        var id = Guid.NewGuid();
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Events.StartStream<T>(id, new ClaimedCounterStarted());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private Task<IScenarioResult> postAsync<T>(string url, T body, string key) where T : class
        => theHost.Scenario(x =>
        {
            x.Post.Json(body).ToUrl(url);
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(200);
        });

    private string sourceFor(string route)
        => theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == route).SourceCode.ShouldNotBeNull();

    [Fact]
    public async Task the_response_is_the_aggregate_as_the_commit_wrote_it()
    {
        var id = await startAsync<ClaimedCounter>();
        var key = Guid.NewGuid().ToString();

        var first = await postAsync("/claimed-counter/increment", new IncrementClaimedCounter(id), key);
        var response = (await first.ReadAsJsonAsync<ClaimedCounter>()).ShouldNotBeNull();

        response.Count.ShouldBe(1);
        response.Version.ShouldBe(2);

        // Versions, timestamps and metadata come from ProjectLatestForCommit, and the commit keeps them
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var stored = (await session.Events.FetchLatest<ClaimedCounter>(id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        stored.Version.ShouldBe(response.Version);
        stored.LastIncrementedAt.ShouldBe(response.LastIncrementedAt);

        var source = sourceFor("/claimed-counter/increment");
        source.ShouldContain(".ProjectLatestForCommit<");
        source.ShouldContain(".QueueClaim(");
        source.ShouldNotContain(".FetchLatest<");
    }

    [Fact]
    public async Task a_repeat_returns_the_first_response_and_the_handler_runs_once()
    {
        var id = await startAsync<ClaimedCounter>();
        var key = Guid.NewGuid().ToString();

        var first = await postAsync("/claimed-counter/increment", new IncrementClaimedCounter(id), key);
        var repeat = await postAsync("/claimed-counter/increment", new IncrementClaimedCounter(id), key);

        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());
        repeat.Context.Response.Headers["Idempotent-Replayed"].ToString().ShouldBe("true");

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task an_inline_snapshot_is_built_before_the_commit_too()
    {
        var id = await startAsync<ClaimedInlineCounter>();
        var key = Guid.NewGuid().ToString();

        var first = await postAsync("/claimed-counter/increment-inline", new IncrementClaimedInlineCounter(id), key);
        var response = (await first.ReadAsJsonAsync<ClaimedInlineCounter>()).ShouldNotBeNull();

        response.Count.ShouldBe(1);
        response.Version.ShouldBe(2);

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var stored = (await session.LoadAsync<ClaimedInlineCounter>(id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        stored.Count.ShouldBe(1);
        stored.Version.ShouldBe(2);

        sourceFor("/claimed-counter/increment-inline").ShouldContain(".ProjectLatestForCommit<");
    }

    [Fact]
    public async Task a_custom_response_can_be_produced_before_the_commit()
    {
        var id = await startAsync<ClaimedCounter>();
        var key = Guid.NewGuid().ToString();

        var first = await postAsync("/claimed-counter/pending", new IncrementClaimedCounter(id), key);

        // Read before the commit, so the session still has this request's event pending
        (await first.ReadAsJsonAsync<PendingEventCount>()).ShouldNotBeNull().Count.ShouldBe(1);

        var source = sourceFor("/claimed-counter/pending");
        source.ShouldContain(".QueueClaim(");
        source.IndexOf("PendingEventCount.Read(", StringComparison.Ordinal)
            .ShouldBeLessThan(source.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal));
    }
}

/// <summary>
/// GH-5721. Under <c>EventAppendMode.Quick</c> the database assigns the timestamp of an event appended to an existing
/// stream, so Marten's <c>ProjectLatestForCommit</c> refuses it and the endpoint keeps claim-and-release.
/// </summary>
public class updated_aggregate_claim_in_transaction_under_quick_append : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.EnableDeduplicatedResponses = true;

            opts.Discovery.DisableConventionalDiscovery();
            opts.Policies.AutoApplyTransactions();
            opts.Discovery.IncludeAssembly(typeof(updated_aggregate_claim_in_transaction).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "http_updated_aggregate_quick";
            m.DisableNpgsqlLogging = true;
            m.Events.AppendMode = EventAppendMode.Quick;

            m.Projections.Snapshot<ClaimedInlineCounter>(SnapshotLifecycle.Inline);
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.ThrowWhenClaimInTransactionFallsBack = true;

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not an UpdatedAggregate claim test endpoint",
                    type => type != typeof(ClaimedCounterEndpoints)));
        }));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public void updated_aggregate_cannot_claim_in_its_transaction()
    {
        var graph = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.Chains.Single(x => x.RoutePattern!.RawText == "/claimed-counter/increment");

        var ex = Should.Throw<InvalidOperationException>(() =>
            chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, theHost.Services));

        ex.Message.ShouldContain("/claimed-counter/increment");
        ex.Message.ShouldContain("EventAppendMode.Quick");
    }
}

public record ClaimedCounterStarted;

public record ClaimedCounterIncremented;

public record IncrementClaimedCounter(Guid ClaimedCounterId);

public record IncrementClaimedInlineCounter(Guid ClaimedInlineCounterId);

public class ClaimedCounter
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public int Count { get; set; }
    public DateTimeOffset LastIncrementedAt { get; set; }

    public void Apply(ClaimedCounterStarted _)
    {
    }

    public void Apply(IEvent<ClaimedCounterIncremented> e)
    {
        Count++;
        LastIncrementedAt = e.Timestamp;
    }
}

public class ClaimedInlineCounter
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public int Count { get; set; }

    public void Apply(ClaimedCounterStarted _)
    {
    }

    public void Apply(ClaimedCounterIncremented _) => Count++;
}

/// <summary>
/// A custom response that reads this transaction's work, produced before the Marten commit.
/// </summary>
public class PendingEventCount : IResponseAware
{
    public int Count { get; set; }

    #region sample_use_for_response_before_commit

    public static void ConfigureResponse(IChain chain)
    {
        // Before the chain commits its Marten session, so a [DeduplicatedWithResponse] endpoint can
        // store this response with a claim written in the same transaction
        chain.UseForResponseBeforeCommit(new MethodCall(typeof(PendingEventCount), nameof(Read)));
    }

    #endregion

    public static PendingEventCount Read(IDocumentSession session)
        => new() { Count = session.PendingChanges.Streams().Sum(x => x.Events.Count) };
}

public static class ClaimedCounterEndpoints
{
    #region sample_updated_aggregate_claim_in_transaction

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/claimed-counter/increment")]
    public static (UpdatedAggregate, Events) Increment(IncrementClaimedCounter command,
        [WriteAggregate] IEventStream<ClaimedCounter> stream)
        => (new UpdatedAggregate(), [new ClaimedCounterIncremented()]);

    #endregion

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/claimed-counter/increment-inline")]
    public static (UpdatedAggregate<ClaimedInlineCounter>, Events) IncrementInline(IncrementClaimedInlineCounter command,
        [WriteAggregate] IEventStream<ClaimedInlineCounter> stream)
        => (new UpdatedAggregate<ClaimedInlineCounter>(), [new ClaimedCounterIncremented()]);

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/claimed-counter/pending")]
    public static (PendingEventCount, Events) Pending(IncrementClaimedCounter command,
        [WriteAggregate] IEventStream<ClaimedCounter> stream)
        => (new PendingEventCount(), [new ClaimedCounterIncremented()]);
}
