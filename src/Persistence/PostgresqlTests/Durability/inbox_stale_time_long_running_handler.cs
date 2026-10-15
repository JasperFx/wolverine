using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace PostgresqlTests.Durability;

/// <summary>
/// Follow-up to GH-5022. <see cref="DurabilitySettings.InboxStaleTime" /> releases an owned inbox row once its
/// stale clock -- restarted when a node claims the row -- is older than the setting. Nothing touches the row
/// while its handler runs, so a handler that runs longer than InboxStaleTime has its row released underneath it,
/// recovery hands it straight back, and a second copy starts while the first is still running.
/// </summary>
public class inbox_stale_time_long_running_handler : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        SlowStaleHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Short enough that the handler below outlives it several times over, with the stale sweep and
                // recovery running often enough to notice
                opts.Durability.InboxStaleTime = 2.Seconds();
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "stale_heartbeat");

                opts.Discovery.DisableConventionalDiscovery().IncludeType<SlowStaleHandler>();
                opts.PublishMessage<SlowStaleMessage>().ToLocalQueue("slow_stale").UseDurableInbox();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_handler_running_longer_than_the_stale_time_runs_once()
    {
        await _host.MessageBus().PublishAsync(new SlowStaleMessage(Guid.NewGuid()));

        // The handler runs for 6 seconds, three times the stale time
        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (SlowStaleHandler.Completed == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }

        // Give a released and recovered copy time to start
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        SlowStaleHandler.Completed.ShouldBeGreaterThan(0);
        SlowStaleHandler.Started.ShouldBe(1,
            $"started {SlowStaleHandler.Started} time(s), at most {SlowStaleHandler.MaxConcurrent} at once");
    }
}

public record SlowStaleMessage(Guid Id);

public class SlowStaleHandler
{
    private static int _started;
    private static int _completed;
    private static int _running;
    private static int _maxConcurrent;

    public static int Started => Volatile.Read(ref _started);
    public static int Completed => Volatile.Read(ref _completed);
    public static int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    public static void Reset()
    {
        _started = 0;
        _completed = 0;
        _running = 0;
        _maxConcurrent = 0;
    }

    public static async Task Handle(SlowStaleMessage message)
    {
        Interlocked.Increment(ref _started);
        var running = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _maxConcurrent)) < running &&
               Interlocked.CompareExchange(ref _maxConcurrent, running, seen) != seen)
        {
        }

        try
        {
            await Task.Delay(6.Seconds());
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            Interlocked.Increment(ref _completed);
        }
    }
}
