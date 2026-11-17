using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
///     GH-5134 audit. .NET 11 makes <c>IHost.RunAsync</c> / <c>IHost.StopAsync</c> throw when a
///     <see cref="BackgroundService" /> has faulted, where earlier frameworks swallowed it. An application's own
///     faulted hosted service must not stop Wolverine from shutting down cleanly: whether the host
///     rethrows the failure or not (it depends on the framework), Wolverine's runtime still has to have
///     stopped by the time <c>StopAsync</c> returns or throws. Wolverine's own BackgroundServices
///     (heartbeat, claim check sweeper, the broker dead letter queue listeners) catch everything
///     except cancellation inside ExecuteAsync for the same reason, so none of them is the faulted one.
/// </summary>
public class faulted_background_service_does_not_block_wolverine_shutdown
{
    [Fact]
    public async Task wolverine_still_stops_when_another_background_service_has_faulted()
    {
        var faulting = new FaultingService();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Keep the host alive past the fault so StopAsync is what observes it
                services.Configure<HostOptions>(x =>
                    x.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
                services.AddSingleton<IHostedService>(faulting);
            })
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery())
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();

        await faulting.Faulted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        runtime.Cancellation.IsCancellationRequested.ShouldBeFalse();

        try
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception e) when (e is InvalidOperationException or AggregateException)
        {
            // .NET 11: the host reports the faulted service. That is the framework's contract, not a Wolverine failure.
        }

        runtime.Cancellation.IsCancellationRequested.ShouldBeTrue();
    }

    private class FaultingService : BackgroundService
    {
        private readonly TaskCompletionSource _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Faulted => _faulted.Task;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Yield first: a synchronous throw would fail StartAsync rather than fault the executing task
            await Task.Yield();
            _faulted.TrySetResult();
            throw new InvalidOperationException("boom from an application background service");
        }
    }
}
