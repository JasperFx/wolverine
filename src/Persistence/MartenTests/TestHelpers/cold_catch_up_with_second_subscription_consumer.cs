using IntegrationTests;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.TestHelpers;

/// <summary>
/// GH-3388, second hypothesis: a SECOND consumer of the Wolverine-managed event subscription
/// distribution (the reporter runs CritterWatch, whose subscription agents share the same
/// EventSubscriptionAgentFamily) leaves the app's projection shard un-started, so the COLD
/// PauseThenCatchUp never gets driven. Simulated here with an extra Wolverine-distributed
/// ancillary store, running under Balanced durability so real agent assignment happens.
/// </summary>
public interface IWatcherStore : IDocumentStore;

public class cold_catch_up_with_second_subscription_consumer : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters_cold5";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);

                // The application's ancillary store with the async projection under test
                opts.Services.AddMartenStore<ILetterStore>(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters_cold6";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine();

                // Stand-in for CritterWatch: a SECOND store contributing async subscription agents
                // to the same Wolverine-managed distribution
                opts.Services.AddMartenStore<IWatcherStore>(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters_cold7";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine();

                // Real agent assignment, not Solo
                opts.Durability.Mode = DurabilityMode.Balanced;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task cold_catch_up_on_ancillary_store_with_second_consumer()
    {
        var id = Guid.NewGuid();

        var tracked = await _host.TrackActivity()
            .ResetAllMartenDataFirst<ILetterStore>()
            .PauseThenCatchUpOnMartenDaemonActivity<ILetterStore>(CatchUpMode.AndDoNothing)
            .InvokeMessageAndWaitAsync(new AppendLetters2(id, ["AAAACCCCBDEEE", "ABCDECCC", "BBBA", "DDDAE"]));

        await using var session = _host.DocumentStore<ILetterStore>().LightweightSession();
        var counts = (await session.Query<LetterCounts>().ToListAsync()).Single();
        counts.Id.ShouldBe(id);

        counts.ACount.ShouldBe(7);
        counts.BCount.ShouldBe(5);
        counts.CCount.ShouldBe(8);
    }

    [Fact]
    public async Task cold_catch_up_on_main_store_with_second_consumer()
    {
        var id = Guid.NewGuid();

        var tracked = await _host.TrackActivity()
            .ResetAllMartenDataFirst()
            .PauseThenCatchUpOnMartenDaemonActivity(CatchUpMode.AndDoNothing)
            .InvokeMessageAndWaitAsync(new AppendLetters(id, ["AAAACCCCBDEEE", "ABCDECCC", "BBBA", "DDDAE"]));

        await using var session = _host.DocumentStore().LightweightSession();
        var counts = (await session.Query<LetterCounts>().ToListAsync()).Single();
        counts.Id.ShouldBe(id);

        counts.ACount.ShouldBe(7);
        counts.BCount.ShouldBe(5);
        counts.CCount.ShouldBe(8);
    }
}
