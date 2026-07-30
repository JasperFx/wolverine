using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests.TestHelpers;

/// <summary>
/// GH-3697: under <c>UseWolverineManagedEventSubscriptionDistribution</c> the Marten store runs in
/// <c>DaemonMode.ExternallyManaged</c>, so Marten's own <c>ForceAllMartenDaemonActivityToCatchUpAsync</c>
/// degrades to a passive read-only wait (marten#4904) and explicitly punts active catch-up to the external
/// coordinator. Wolverine already implemented that path, but only as a <c>TrackActivity()</c> stage
/// (<c>PauseThenCatchUpOnMartenDaemonActivity</c>). A suite that is not driving the work through a tracked
/// session had nothing to call, so every one of them reimplemented "pause the coordinator, then
/// <c>StopAllAsync</c> + <c>CatchUpAsync</c> each daemon" by hand — which races the coordinator and throws
/// <c>ProgressionProgressOutOfOrderException</c> and duplicate-key errors on <c>pk_mt_event_progression</c>.
///
/// These cover the standalone entry point. It never calls <c>CatchUpAsync</c>: the agents that already own
/// the shards are resumed and drained, so there is only ever one writer of each progression row.
/// </summary>
public class standalone_catch_up_under_wolverine_distribution : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters7";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Services.AddMartenStore<ILetterStore>(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "letters8";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine();

                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task catches_up_the_main_store_with_no_tracked_session()
    {
        await _host.ResetAllMartenDataAsync();

        var id = Guid.NewGuid();

        await using var session = _host.DocumentStore().LightweightSession();
        session.Events.StartStream<LetterCounts>(id, "AAAACCCCBDEEE".ToLetterEvents());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _host.PauseThenCatchUpOnMartenDaemonActivityAsync(
            cancellation: TestContext.Current.CancellationToken);

        var counts = await session.LoadAsync<LetterCounts>(id, TestContext.Current.CancellationToken);
        counts.ShouldNotBeNull();
        counts.ACount.ShouldBe(4);
        counts.BCount.ShouldBe(1);
        counts.CCount.ShouldBe(4);
    }

    [Fact]
    public async Task catches_up_an_ancillary_store_with_no_tracked_session()
    {
        await _host.ResetAllMartenDataAsync<ILetterStore>();

        var id = Guid.NewGuid();

        await using var session = _host.DocumentStore<ILetterStore>().LightweightSession();
        session.Events.StartStream<LetterCounts>(id, "AAAACCCCBDEEE".ToLetterEvents());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _host.PauseThenCatchUpOnMartenDaemonActivityAsync<ILetterStore>(
            cancellation: TestContext.Current.CancellationToken);

        var counts = await session.LoadAsync<LetterCounts>(id, TestContext.Current.CancellationToken);
        counts.ShouldNotBeNull();
        counts.ACount.ShouldBe(4);
        counts.CCount.ShouldBe(4);
    }

    /// <summary>
    /// AndDoNothing has to leave the coordinator paused, so a suite can catch up, assert, append more events,
    /// and catch up again without the daemon racing its assertions in between.
    /// </summary>
    [Fact]
    public async Task and_do_nothing_leaves_the_daemons_paused_and_a_second_pass_still_works()
    {
        await _host.ResetAllMartenDataAsync();

        var id = Guid.NewGuid();

        await using var session = _host.DocumentStore().LightweightSession();
        session.Events.StartStream<LetterCounts>(id, "AAAA".ToLetterEvents());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _host.PauseThenCatchUpOnMartenDaemonActivityAsync(CatchUpMode.AndDoNothing,
            cancellation: TestContext.Current.CancellationToken);

        (await session.LoadAsync<LetterCounts>(id, TestContext.Current.CancellationToken))!
            .ACount.ShouldBe(4);

        // Appended while the coordinator is paused -- nothing should be projecting it yet.
        session.Events.Append(id, "BBB".ToLetterEvents());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);

        // A paused coordinator hands back no daemons to drive, which is exactly the state the hand-rolled
        // workaround in the report was fighting. The supported path resumes them instead.
        await _host.PauseThenCatchUpOnMartenDaemonActivityAsync(CatchUpMode.AndDoNothing,
            cancellation: TestContext.Current.CancellationToken);

        var counts = await session.LoadAsync<LetterCounts>(id, TestContext.Current.CancellationToken);
        counts!.ACount.ShouldBe(4);
        counts.BCount.ShouldBe(3);
    }
}
