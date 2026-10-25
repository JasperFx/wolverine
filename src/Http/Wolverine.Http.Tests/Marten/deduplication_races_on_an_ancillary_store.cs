using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Http.Runtime;
using Wolverine.Http.Tests.AncillaryStore;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-5205 and GH-5206 on an ancillary Marten store. Each claim has to be written to, and looked up again in, the
/// ancillary store's message tables: a lookup in the main store's would find nothing, and answer a repeat as if no
/// request with its key had ever succeeded.
/// </summary>
public class deduplication_races_on_an_ancillary_store : IAsyncLifetime
{
    private const string MainSchema = "http_dedup_race_main";
    private const string AncillarySchema = "http_dedup_race_ancillary";

    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.EnableDeduplicatedResponses = true;
            opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
            opts.Durability.DeduplicationWindow = 1.Hours();
            opts.Policies.AutoApplyTransactions();
            opts.Discovery.IncludeAssembly(typeof(AncillaryArrivals).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = MainSchema;
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddMartenStore<INkThingStore>(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = AncillarySchema;
            m.DisableNpgsqlLogging = true;

            // The natural-key endpoints in the same assembly need it
            m.Projections.Snapshot<NkThing>(SnapshotLifecycle.Inline);
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddResourceSetupOnStartup();
        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints());
        await ((IHost)theHost).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task a_write_aggregate_of_the_aggregate_itself_claims_in_the_ancillary_transaction()
    {
        // GH-5205. The loser of the race fails on the stream version, and is answered from the winner's claim, which
        // is only found in the ancillary store
        var counterId = Guid.NewGuid();
        await using (var session = theHost.Services.GetRequiredService<INkThingStore>().LightweightSession())
        {
            session.Events.StartStream<AncillaryCounter>(counterId, new AncillaryCounterStarted());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = Guid.NewGuid().ToString();
        var command = new IncrementAncillaryCounter(counterId, "race-" + key);

        var responses = await Task.WhenAll(
            postAsync("/ancillary-dedup/response/counter", command, key, 200),
            postAsync("/ancillary-dedup/response/counter", command, key, 200));

        AncillaryArrivals.Arrivals(command.Note).ShouldBe(2);
        (await responses[1].ReadAsTextAsync()).ShouldBe(await responses[0].ReadAsTextAsync());

        // And a later repeat is answered up front
        var repeat = await postAsync("/ancillary-dedup/response/counter", command, key, 200);
        (await repeat.ReadAsTextAsync()).ShouldBe(await responses[0].ReadAsTextAsync());

        await using var query = theHost.Services.GetRequiredService<INkThingStore>().QuerySession();
        var events = await query.Events.FetchStreamAsync(counterId, token: TestContext.Current.CancellationToken);
        events.Count(x => x.Data is AncillaryCounterIncremented).ShouldBe(1);

        (await responseClaimAsync(ancillary: true, key)).ShouldNotBeNull();
        (await responseClaimAsync(ancillary: false, key)).ShouldBeNull();

        var source = sourceFor("/ancillary-dedup/response/counter");
        source.ShouldContain(".QueueClaim(");
        source.ShouldNotContain(".TryClaimAsync(");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_concurrent_repeat_refused_over_committed_work_is_answered_from_the_ancillary_claim(bool throws)
    {
        // GH-5206, [DeduplicatedWithResponse]
        var key = Guid.NewGuid().ToString();
        var command = new OpenAncillaryAccount("response-admission-" + key, throws);

        var requests = new[]
        {
            postAsync("/ancillary-dedup/response/accounts", command, key, 200),
            postAsync("/ancillary-dedup/response/accounts", command, key, 200)
        };

        await Task.WhenAny(requests);
        AncillaryArrivals.Release(command.Name);
        var responses = await Task.WhenAll(requests);

        AncillaryArrivals.Arrivals(command.Name).ShouldBe(2);
        (await responses[1].ReadAsTextAsync()).ShouldBe(await responses[0].ReadAsTextAsync());

        (await responseClaimAsync(ancillary: true, key)).ShouldNotBeNull();
        (await responseClaimAsync(ancillary: false, key)).ShouldBeNull();
        (await accountsAsync(command.Name)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_concurrent_repeat_refused_over_committed_work_is_refused_as_a_duplicate_from_the_ancillary_claim(
        bool throws)
    {
        // GH-5206, [Deduplicated]
        var key = Guid.NewGuid().ToString();
        var command = new OpenAncillaryAccount("dedup-admission-" + key, throws);

        var requests = new[]
        {
            postAnyAsync("/ancillary-dedup/accounts", command, key),
            postAnyAsync("/ancillary-dedup/accounts", command, key)
        };

        await Task.WhenAny(requests);
        AncillaryArrivals.Release(command.Name);
        var results = await Task.WhenAll(requests);

        AncillaryArrivals.Arrivals(command.Name).ShouldBe(2);
        results.Select(x => x.Context.Response.StatusCode).Order().ShouldBe([200, 409]);

        var refused = results.Single(x => x.Context.Response.StatusCode == 409);
        (await refused.ReadAsJsonAsync<ProblemDetails>())!.Detail
            .ShouldBe("A request with this 'Idempotency-Key' has already been handled");

        (await claimCountAsync(AncillarySchema, key)).ShouldBe(1);
        (await claimCountAsync(MainSchema, key)).ShouldBe(0);
        (await accountsAsync(command.Name)).ShouldBe(1);
    }

    private string sourceFor(string route)
        => theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == route).SourceCode.ShouldNotBeNull();

    // DeduplicationScope.User, for an anonymous caller
    private Task<DeduplicatedResponseClaim?> responseClaimAsync(bool ancillary, string key)
    {
        var runtime = theHost.Services.GetRequiredService<IWolverineRuntime>();
        var store = (IReplayableDeduplicationStore)(ancillary
            ? runtime.Stores.FindAncillaryStore(typeof(INkThingStore))
            : runtime.Storage);

        var storageId = DeduplicatedResponses.StorageIdFor(
            HttpHandler.ScopeDeduplicationId(new DefaultHttpContext(), key, DeduplicationScope.User, null)!);
        return store.FindAsync(storageId, TestContext.Current.CancellationToken);
    }

    private async Task<int> accountsAsync(string name)
    {
        await using var query = theHost.Services.GetRequiredService<INkThingStore>().QuerySession();
        return await query.Query<AncillaryAccount>().CountAsync(x => x.Name == name,
            TestContext.Current.CancellationToken);
    }

    private static async Task<long> claimCountAsync(string schema, string key)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {schema}.wolverine_deduplication_hashed where deduplication_id = @id";
        cmd.Parameters.AddWithValue("id", key);

        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private Task<IScenarioResult> postAsync<T>(string url, T body, string key, int status) where T : class
        => theHost.Scenario(x =>
        {
            x.Post.Json(body).ToUrl(url);
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(status);
        });

    private Task<IScenarioResult> postAnyAsync<T>(string url, T body, string key) where T : class
        => theHost.Scenario(x =>
        {
            x.Post.Json(body).ToUrl(url);
            x.WithRequestHeader("Idempotency-Key", key);
            x.IgnoreStatusCode();
        });
}
