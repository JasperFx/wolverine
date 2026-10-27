using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Persistence;

// GH-5235. A metrics query the sweeper already has in flight used to outlive host shutdown and
// agent removal, then run against a store (and data source) that had been disposed. Both tests
// gate FetchCountsAsync() so the in-flight query is held for as long as the test wants.
public class persistence_metrics_sweeper_lifetime
{
    [Fact]
    public async Task host_shutdown_waits_for_an_in_flight_metrics_query()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.MediatorOnly;
                opts.Durability.UpdateMetricsPeriod = 10.Milliseconds();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var store = new GatedStore("shutdown");

        using var metrics = new PersistenceMetrics(runtime, runtime.Options.Durability, "shutdown");
        using var registration = PersistenceMetricsSweeper.For(runtime).Register(store.Store, metrics);

        await store.Entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var stopping = host.StopAsync(TestContext.Current.CancellationToken);

        // The query is still running against the store, so shutdown must not get past it
        await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        stopping.IsCompleted.ShouldBeFalse();

        store.Release();
        await stopping.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        store.Finished.Task.IsCompleted.ShouldBeTrue();

        // And nothing touches the store once the host has stopped
        var fetches = store.Fetches;
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        store.Fetches.ShouldBe(fetches);
    }

    [Fact]
    public async Task removing_a_registration_waits_for_its_in_flight_query_and_stops_polling_it()
    {
        var runtime = new CoreTests.Runtime.MockWolverineRuntime();
        runtime.Options.Durability.UpdateMetricsPeriod = 10.Milliseconds();

        var store = new GatedStore("removed");
        var metrics = new PersistenceMetrics(runtime, runtime.Options.Durability, "removed");
        var registration = PersistenceMetricsSweeper.For(runtime).Register(store.Store, metrics);

        await store.Entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // DurabilityAgent.StopAsync() removes its registration this way
        var removing = ((IAsyncDisposable)registration).DisposeAsync().AsTask();

        await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        removing.IsCompleted.ShouldBeFalse();

        store.Release();
        await removing.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        store.Finished.Task.IsCompleted.ShouldBeTrue();

        var fetches = store.Fetches;
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        store.Fetches.ShouldBe(fetches);
    }

    public class GatedStore
    {
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _fetches;

        public GatedStore(string name)
        {
            var admin = Substitute.For<IMessageStoreAdmin>();
            admin.FetchCountsAsync().Returns(_ => fetchAsync());

            Store = Substitute.For<IMessageStore>();
            Store.Uri.Returns(new Uri($"wolverinedb://test/{name}"));
            Store.Admin.Returns(admin);
        }

        public IMessageStore Store { get; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Fetches => Volatile.Read(ref _fetches);

        public void Release() => _resume.TrySetResult();

        private async Task<PersistedCounts> fetchAsync()
        {
            Interlocked.Increment(ref _fetches);
            Entered.TrySetResult();

            try
            {
                await _resume.Task;
                return new PersistedCounts();
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }
}
