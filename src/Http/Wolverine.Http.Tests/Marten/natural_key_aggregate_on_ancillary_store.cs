using Alba;
using IntegrationTests;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Tests.AncillaryStore;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
///     GH-4439 over HTTP: an endpoint routed to an ancillary store with <c>[MartenStore]</c>, taking a
///     natural-key-identified aggregate that is registered only on that store.
/// </summary>
/// <remarks>
///     <para>
///     Worth having separately from the message-handler suites because the two chain types resolve their
///     store at opposite points, and the HTTP one is the hostile case. A handler chain runs parameter
///     attributes before chain attributes but is rescued by the Phase-A eager policies, which pre-assign
///     <c>IChain.AncillaryStoreType</c> at <c>HandlerGraph.Compile</c>. An HTTP chain gets no such rescue:
///     <c>HttpChain.MapToRoute</c> triggers parameter matching from the <c>[WolverinePost]</c> attribute in
///     the constructor, long before <c>applyAttributesAndConfigureMethods</c> applies <c>[MartenStore]</c>,
///     and the eager policies are <c>IHandlerPolicy</c> so they never see an endpoint at all. The property
///     is still null at that moment; only <c>chain.DetermineAncillaryStoreType()</c> answers correctly.
///     </para>
///     <para>
///     The endpoints live in <c>Wolverine.Http.Tests.AncillaryStore</c> and this host is the only one that
///     discovers them — see that project's csproj. An endpoint whose parameter matching needs a store the
///     host has not registered throws from <c>HttpGraph.DiscoverEndpoints</c> and takes the whole host with
///     it, so these cannot live in the sample app or in this test assembly.
///     </para>
/// </remarks>
public class natural_key_aggregate_on_ancillary_store : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            // The only host in the suite that opts into these endpoints.
            opts.Discovery.IncludeAssembly(typeof(NkThingEndpoints).Assembly);

            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.MessageStorageSchemaName = "nk_http_wolverine";
            opts.Policies.AutoApplyTransactions();
        });

        // The main store knows NOTHING about the aggregate -- if natural-key detection asks this store, it
        // finds nothing and the workflow reports that it cannot determine an aggregate id.
        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "nk_http_main";
            opts.Events.DatabaseSchemaName = "nk_http_main";
            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddMartenStore<INkThingStore>(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "nk_http_things";
            opts.Events.DatabaseSchemaName = "nk_http_things";
            opts.DisableNpgsqlLogging = true;
            opts.Projections.Snapshot<NkThing>(SnapshotLifecycle.Inline);
        }).IntegrateWithWolverine();

        builder.Services.AddResourceSetupOnStartup();
        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => { app.MapWolverineEndpoints(); });
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        await theHost.DisposeAsync();
    }

    // The natural-key lookup table outlives the run and a natural key names exactly ONE stream, so a
    // hardcoded key would pass once and then fail every rerun with DuplicateNaturalKeyException.
    private static ThingCode uniqueCode() => new("NK-" + Guid.NewGuid().ToString("N"));

    private async Task<(Guid StreamId, ThingCode Code)> seedThing(string title)
    {
        var streamId = Guid.NewGuid();
        var code = uniqueCode();

        var store = theHost.Services.GetRequiredService<INkThingStore>();
        await using var session = store.LightweightSession();
        session.Events.StartStream<NkThing>(streamId, new NkThingCreated(code, title));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (streamId, code);
    }

    private async Task<NkThing?> load(Guid streamId)
    {
        var store = theHost.Services.GetRequiredService<INkThingStore>();
        await using var session = store.LightweightSession();
        return await session.LoadAsync<NkThing>(streamId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task write_aggregate_endpoint_resolves_the_natural_key_through_the_ancillary_store()
    {
        var (streamId, code) = await seedThing("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkThing(code, "Renamed")).ToUrl("/nkthings/rename");

            // 204 comes from [EmptyResponse]. The status alone proves little -- without that attribute the
            // endpoint answers 200 and appends nothing at all -- so the document assertion is the real test.
            s.StatusCodeShouldBe(204);
        });

        var thing = await load(streamId);
        thing.ShouldNotBeNull();
        thing.Title.ShouldBe("Renamed");
    }

    [Fact]
    public async Task write_model_endpoint_resolves_the_natural_key_through_the_ancillary_store()
    {
        var (streamId, code) = await seedThing("Archive me");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new ArchiveNkThing(code)).ToUrl("/nkthings/archive");
            s.StatusCodeShouldBe(204);
        });

        var thing = await load(streamId);
        thing.ShouldNotBeNull();
        thing.IsArchived.ShouldBeTrue();
    }
}
