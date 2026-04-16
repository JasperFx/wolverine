using IntegrationTests;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

/// <summary>
/// Regression tests for https://github.com/JasperFx/wolverine/issues/2337
/// When multiple Marten stores live in the same assembly, each store's SubjectUri
/// must be unique so JasperFx can generate separate database schemas per store.
/// </summary>
public class ancillary_store_subject_uri_uniqueness : IAsyncLifetime
{
    private IHost theHost;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Primary Marten store
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                // Ancillary store 1 – same database, different schema
                opts.Services.AddMartenStore<IPlayerStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "players";
                }).IntegrateWithWolverine();

                // Ancillary store 2 – same database, different schema
                opts.Services.AddMartenStore<IThingStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "things";
                }).IntegrateWithWolverine();

                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public void every_store_should_have_a_unique_subject_uri()
    {
        var runtime = theHost.GetRuntime();

        // Primary store SubjectUri
        var primarySubjectUri = runtime.Stores.Main
            .As<MessageDatabase<NpgsqlConnection>>()
            .SubjectUri;

        var ancillaries = theHost.Services.GetServices<AncillaryMessageStore>().ToList();

        // Ancillary store 1 (IPlayerStore)
        var playerStore = ancillaries.Single(x => x.MarkerType == typeof(IPlayerStore));
        var playerSubjectUri = playerStore.Inner
            .As<MessageDatabase<NpgsqlConnection>>()
            .SubjectUri;

        // Ancillary store 2 (IThingStore)
        var thingStore = ancillaries.Single(x => x.MarkerType == typeof(IThingStore));
        var thingSubjectUri = thingStore.Inner
            .As<MessageDatabase<NpgsqlConnection>>()
            .SubjectUri;

        // All three SubjectUris must be distinct so JasperFx can generate
        // separate schema migration scripts per store (issue #2337)
        playerSubjectUri.ShouldNotBe(primarySubjectUri);
        thingSubjectUri.ShouldNotBe(primarySubjectUri);
        thingSubjectUri.ShouldNotBe(playerSubjectUri);
    }
}
