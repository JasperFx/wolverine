using Alba;
using IntegrationTests;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
/// GH-5160. A main and an ancillary Marten store handed ONE <see cref="NpgsqlDataSource" />, each keeping its
/// message tables in its own schema. A claim written inside an ancillary endpoint's transaction must land in the
/// ancillary store's table, where it is read back, even after the main store's endpoint has claimed through the
/// same data source.
/// </summary>
public class deduplicated_with_response_on_stores_sharing_a_data_source : IAsyncLifetime
{
    private NpgsqlDataSource theDataSource = null!;
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);

        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.EnableDeduplicatedResponses = true;
            opts.Policies.AutoApplyTransactions();
            opts.Discovery.IncludeAssembly(typeof(SharedDataSourceDeduplicatedResponseEndpoints).Assembly);
        });

        // No MessageStorageSchemaName, so each store's message tables follow its own schema
        builder.Services.AddMarten(m =>
        {
            m.Connection(theDataSource);
            m.DatabaseSchemaName = "http_dedup_shared_main";
            m.Events.DatabaseSchemaName = "http_dedup_shared_main";
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddMartenStore<INkThingStore>(m =>
        {
            m.Connection(theDataSource);
            m.DatabaseSchemaName = "http_dedup_shared_ancillary";
            m.Events.DatabaseSchemaName = "http_dedup_shared_ancillary";
            m.DisableNpgsqlLogging = true;

            // The natural-key endpoints in the same assembly need it
            m.Projections.Snapshot<NkThing>(SnapshotLifecycle.Inline);
        }).IntegrateWithWolverine();

        builder.Services.AddResourceSetupOnStartup();
        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints());
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        await theHost.DisposeAsync();
        await theDataSource.DisposeAsync();
    }

    [Fact]
    public async Task an_ancillary_claim_lands_in_its_own_store_after_the_main_store_claimed_through_the_same_data_source()
    {
        // The main store claims first, through the same data source
        await postAsync("/shared-source-deduplicated-response/main", new SharedSourceOrder("main"),
            Guid.NewGuid().ToString(), 200);

        var key = Guid.NewGuid().ToString();
        var request = new SharedSourceOrder("ancillary-" + key);

        var first = await postAsync("/shared-source-deduplicated-response/ancillary", request, key, 200);

        var runtime = theHost.Services.GetRequiredService<IWolverineRuntime>();
        var ancillary = (IReplayableDeduplicationStore)runtime.Stores.FindAncillaryStore(typeof(INkThingStore));
        var main = (IReplayableDeduplicationStore)runtime.Storage;

        (await ancillary.FindAsync(storageIdFor(key), TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await main.FindAsync(storageIdFor(key), TestContext.Current.CancellationToken)).ShouldBeNull();

        var repeat = await postAsync("/shared-source-deduplicated-response/ancillary", request, key, 200);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());
        SharedDataSourceDeduplicatedResponseEndpoints.Runs(request.Name).ShouldBe(1);
    }

    // DeduplicationScope.User, for an anonymous caller
    private static string storageIdFor(string key)
        => DeduplicatedResponses.StorageIdFor(
            HttpHandler.ScopeDeduplicationId(new DefaultHttpContext(), key, DeduplicationScope.User, null)!);

    private Task<IScenarioResult> postAsync<T>(string url, T body, string key, int status) where T : class
        => theHost.Scenario(x =>
        {
            x.Post.Json(body).ToUrl(url);
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(status);
        });
}
