using IntegrationTests;
using JasperFx.Events.Daemon;
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
/// GH-3388: the tracked PauseThenCatchUpOnMartenDaemonActivity is the very FIRST daemon interaction
/// against a COLD daemon whose shards were never started. No warming seed, no prior
/// WaitForNonStaleProjectionDataAsync.
/// </summary>
public class cold_catch_up_with_wolverine_distribution : IAsyncLifetime
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
                    m.DatabaseSchemaName = "letters_cold3";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Services.AddMartenStore<ILetterStore>(m =>
                {
                    m.DisableNpgsqlLogging = true;

                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters_cold4";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine();

                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task cold_catch_up_with_main_store()
    {
        var id = Guid.NewGuid();

        // NO warming. This is the first daemon interaction of the host.
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

    [Fact]
    public async Task cold_catch_up_with_ancillary_store()
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
}
