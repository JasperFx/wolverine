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
using Wolverine.Tracking;

namespace MartenTests.TestHelpers;

public interface ISecondLetterStore : IDocumentStore;

/// <summary>
/// GH-5317: one call that catches up the main store and every ancillary store, so a shared test helper
/// does not need to know the ancillary store types
/// </summary>
public class catch_up_on_all_marten_stores : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "gh5317_main";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Services.AddMartenStore<ILetterStore>(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "gh5317_first";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<ISecondLetterStore>(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "gh5317_second";

                    m.Projections.Add<LetterCountsProjection>(ProjectionLifecycle.Async);
                }).IntegrateWithWolverine();

                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        await _host.ResetAllMartenDataAsync();
        await _host.ResetAllMartenDataAsync<ILetterStore>();
        await _host.ResetAllMartenDataAsync<ISecondLetterStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static async Task<Guid> startStreamAsync(IDocumentStore store, string letters)
    {
        var id = Guid.NewGuid();
        await using var session = store.LightweightSession();
        session.Events.StartStream<LetterCounts>(id, letters.ToLetterEvents());
        await session.SaveChangesAsync();
        return id;
    }

    private static async Task<LetterCounts> loadAsync(IDocumentStore store, Guid id)
    {
        await using var session = store.QuerySession();
        var counts = await session.LoadAsync<LetterCounts>(id);
        counts.ShouldNotBeNull();
        return counts;
    }

    [Fact]
    public async Task catches_up_the_main_store_and_every_ancillary_store()
    {
        var mainId = await startStreamAsync(_host.DocumentStore(), "AAAACCCCB");
        var firstId = await startStreamAsync(_host.DocumentStore<ILetterStore>(), "AABBB");
        var secondId = await startStreamAsync(_host.DocumentStore<ISecondLetterStore>(), "CCCCCD");

        #region sample_pause_then_catch_up_on_all_marten_daemon_activity

        // Catches up the main Marten store and every ancillary store
        // integrated with Wolverine, without naming the ancillary store types
        await _host.PauseThenCatchUpOnAllMartenDaemonActivityAsync(
            cancellation: TestContext.Current.CancellationToken);

        #endregion

        (await loadAsync(_host.DocumentStore(), mainId)).ACount.ShouldBe(4);
        (await loadAsync(_host.DocumentStore<ILetterStore>(), firstId)).BCount.ShouldBe(3);
        (await loadAsync(_host.DocumentStore<ISecondLetterStore>(), secondId)).CCount.ShouldBe(5);
    }

    [Fact]
    public async Task and_do_nothing_leaves_every_store_paused()
    {
        await _host.PauseThenCatchUpOnAllMartenDaemonActivityAsync(CatchUpMode.AndDoNothing,
            cancellation: TestContext.Current.CancellationToken);

        // Appended while every coordinator is paused
        var firstId = await startStreamAsync(_host.DocumentStore<ILetterStore>(), "AAA");
        var secondId = await startStreamAsync(_host.DocumentStore<ISecondLetterStore>(), "BBB");
        await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);

        await using (var session = _host.DocumentStore<ILetterStore>().QuerySession())
        {
            (await session.LoadAsync<LetterCounts>(firstId, TestContext.Current.CancellationToken)).ShouldBeNull();
        }

        await using (var session = _host.DocumentStore<ISecondLetterStore>().QuerySession())
        {
            (await session.LoadAsync<LetterCounts>(secondId, TestContext.Current.CancellationToken)).ShouldBeNull();
        }

        await _host.PauseThenCatchUpOnAllMartenDaemonActivityAsync(
            cancellation: TestContext.Current.CancellationToken);

        (await loadAsync(_host.DocumentStore<ILetterStore>(), firstId)).ACount.ShouldBe(3);
        (await loadAsync(_host.DocumentStore<ISecondLetterStore>(), secondId)).BCount.ShouldBe(3);
    }

    [Fact]
    public async Task tracked_session_catches_up_every_store()
    {
        var mainId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        #region sample_tracked_session_pause_then_catch_up_on_all_marten_daemon_activity

        await _host.TrackActivity()
            .Timeout(30.Seconds())
            .PauseThenCatchUpOnAllMartenDaemonActivity()
            .ExecuteAndWaitAsync(async Task (IMessageContext _) =>
            {
                await using var main = _host.DocumentStore().LightweightSession();
                main.Events.StartStream<LetterCounts>(mainId, "AAB".ToLetterEvents());
                await main.SaveChangesAsync();

                await using var first = _host.DocumentStore<ILetterStore>().LightweightSession();
                first.Events.StartStream<LetterCounts>(firstId, "ABBB".ToLetterEvents());
                await first.SaveChangesAsync();

                await using var second = _host.DocumentStore<ISecondLetterStore>().LightweightSession();
                second.Events.StartStream<LetterCounts>(secondId, "CCD".ToLetterEvents());
                await second.SaveChangesAsync();
            });

        #endregion

        (await loadAsync(_host.DocumentStore(), mainId)).ACount.ShouldBe(2);
        (await loadAsync(_host.DocumentStore<ILetterStore>(), firstId)).BCount.ShouldBe(3);
        (await loadAsync(_host.DocumentStore<ISecondLetterStore>(), secondId)).CCount.ShouldBe(2);
    }
}
