using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Pubsub.Internal;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public record PubsubInFlightWork(string Batch, int Number);

/// <summary>Per-node, per-batch tracking, so a late recording by a stopped node can never satisfy another's assertion.</summary>
public class PubsubInFlightNode(string name)
{
    public string Name { get; } = name;
}

public static class PubsubInFlightTracking
{
    public static readonly ConcurrentBag<(string Node, string Batch, int Number)> Started = new();
    public static readonly ConcurrentBag<(string Node, string Batch, int Number)> Handled = new();
    public static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new();

    public static int StartedOn(string node, string batch) => Started.Count(x => x.Node == node && x.Batch == batch);

    public static IEnumerable<int> HandledBy(string node, string batch) =>
        Handled.Where(x => x.Node == node && x.Batch == batch).Select(x => x.Number).Distinct().OrderBy(x => x);
}

public class PubsubInFlightWorkHandler
{
    public async Task Handle(PubsubInFlightWork message, PubsubInFlightNode node)
    {
        PubsubInFlightTracking.Started.Add((node.Name, message.Batch, message.Number));

        if (PubsubInFlightTracking.Gates.TryGetValue(node.Name, out var gate))
        {
            await gate.Task;
        }

        PubsubInFlightTracking.Handled.Add((node.Name, message.Batch, message.Number));
    }
}

/// <summary>
/// GH-5533. A Pub/Sub redelivery of a message that is still in flight, with the in-memory idempotency guard on.
/// The subscriber client stops extending the ack deadline after a deliberately tiny MaxTotalAckExtension, so the
/// emulator redelivers each message -- under a new ack id -- to the same listener while the original callback is
/// still held open.
/// </summary>
/// <remarks>
/// Before GH-5533 the guard acked that redelivery as a duplicate, which on Pub/Sub settles the message while its
/// first delivery is still being handled. And because held deliveries were keyed by the Pub/Sub message id, which
/// a redelivery shares, the redelivery's hold also replaced the original's.
/// </remarks>
public class native_ack_inflight_redelivery_5533 : IAsyncLifetime
{
    private bool _skip;

    public async ValueTask InitializeAsync()
    {
        _skip = !await TestingExtensions.IsEmulatorAvailable();
    }

    public ValueTask DisposeAsync()
    {
        foreach (var gate in PubsubInFlightTracking.Gates.Values) gate.TrySetResult();
        PubsubInFlightTracking.Gates.Clear();
        return ValueTask.CompletedTask;
    }

    private static Task<IHost> startHostAsync(string topic, string node, HoldingLoggerProvider? logs = null,
        bool purge = false)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(x =>
            {
                if (logs != null)
                {
                    x.AddProvider(logs);
                    x.AddFilter<HoldingLoggerProvider>("Wolverine.Runtime.WorkerQueues", LogLevel.Debug);
                }
            })
            .UseWolverine(opts =>
            {
                var pubsub = opts.UsePubsubTesting().AutoProvision();
                if (purge) pubsub.AutoPurgeOnStartup();

                opts.Services.AddSingleton(new PubsubInFlightNode(node));
                opts.Discovery.DisableConventionalDiscovery().IncludeType<PubsubInFlightWorkHandler>();
                opts.Durability.DrainTimeout = 2.Seconds();

                opts.PublishMessage<PubsubInFlightWork>().ToPubsubTopic(topic);

                opts.ListenToPubsubTopic(topic)
                    .ProcessInParallelWithNativeAcks()
                    .WithInMemoryIdempotency()
                    // Stop extending the ack deadline almost at once, so the emulator redelivers while the
                    // original callback is still held open
                    .ConfigureListener(c => c.MaxTotalAckExtension = 2.Seconds());
            }).StartAsync();
    }

    private static async Task<IHost> startWithInFlightRedeliveriesAsync(string topic, string node, string batch,
        int count)
    {
        PubsubInFlightTracking.Gates[node] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var logs = new HoldingLoggerProvider();
        var host = await startHostAsync(topic, node, logs, purge: true);

        var bus = host.MessageBus();
        for (var i = 0; i < count; i++)
        {
            await bus.SendAsync(new PubsubInFlightWork(batch, i));
        }

        await waitFor(() => PubsubInFlightTracking.StartedOn(node, batch) >= count, 60.Seconds());
        PubsubInFlightTracking.StartedOn(node, batch).ShouldBe(count);

        // Wait until the emulator has redelivered every one of them while the originals are still held
        await waitFor(() => logs.HeldDuplicates >= count, 90.Seconds());
        logs.HeldDuplicates.ShouldBeGreaterThanOrEqualTo(count);

        // Held, never run a second time
        PubsubInFlightTracking.StartedOn(node, batch).ShouldBe(count);
        PubsubInFlightTracking.HandledBy(node, batch).ShouldBeEmpty();

        return host;
    }

    [Fact]
    public async Task an_in_flight_redelivery_is_not_acked_so_a_draining_node_still_loses_nothing()
    {
        _skip.ShouldBeFalse("The Pub/Sub emulator is not reachable on localhost:8085");

        var topic = "inflight5533drain";
        var batch = Guid.NewGuid().ToString("N");

        var firstHost = await startWithInFlightRedeliveriesAsync(topic, "redelivered", batch, 3);

        await firstHost.StopAsync(TestContext.Current.CancellationToken);
        firstHost.Dispose();
        PubsubInFlightTracking.HandledBy("redelivered", batch).ShouldBeEmpty();

        using var secondHost = await startHostAsync(topic, "after");

        await waitFor(() => PubsubInFlightTracking.HandledBy("after", batch).Count() >= 3, 90.Seconds());
        PubsubInFlightTracking.HandledBy("after", batch).ShouldBe(Enumerable.Range(0, 3));
    }

    [Fact]
    public async Task an_in_flight_redelivery_is_acked_with_its_own_ack_id_once_the_original_succeeds()
    {
        _skip.ShouldBeFalse("The Pub/Sub emulator is not reachable on localhost:8085");

        var topic = "inflight5533settle";
        var batch = Guid.NewGuid().ToString("N");

        var firstHost = await startWithInFlightRedeliveriesAsync(topic, "settling", batch, 3);

        PubsubInFlightTracking.Gates["settling"].TrySetResult();

        await waitFor(() => PubsubInFlightTracking.HandledBy("settling", batch).Count() >= 3, 30.Seconds());
        PubsubInFlightTracking.HandledBy("settling", batch).ShouldBe(Enumerable.Range(0, 3));

        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        await firstHost.StopAsync(TestContext.Current.CancellationToken);
        firstHost.Dispose();

        // Anything left unacknowledged (or nacked by the stop) would come back to this node well inside this window
        using var secondHost = await startHostAsync(topic, "leftovers");
        await Task.Delay(15.Seconds(), TestContext.Current.CancellationToken);

        PubsubInFlightTracking.StartedOn("leftovers", batch).ShouldBe(0);
    }

    [Fact]
    public void two_deliveries_of_the_same_message_are_held_separately()
    {
        var deliveries = new PubsubHeldDeliveries();

        var originalKey = PubsubHeldDeliveries.KeyFor("message-1");
        var redeliveryKey = PubsubHeldDeliveries.KeyFor("message-1");
        originalKey.ShouldNotBe(redeliveryKey);

        var original = deliveries.Hold(originalKey, 1);
        var redelivery = deliveries.Hold(redeliveryKey, 1);

        var originalEnvelope = new Envelope();
        originalEnvelope.Headers[PubsubHeldDeliveries.DeliveryKeyHeader] = originalKey;

        deliveries.TryFind(originalEnvelope, out var found).ShouldBeTrue();
        found.ShouldBeSameAs(original);
        found.Succeeded().ShouldBeTrue();

        original.Reply.IsCompleted.ShouldBeTrue();
        redelivery.Reply.IsCompleted.ShouldBeFalse();
    }

    private static async Task waitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }
}

/// <summary>Counts the native-ack receiver's "holding duplicate delivery" Debug lines.</summary>
internal class HoldingLoggerProvider : ILoggerProvider
{
    private int _held;

    public int HeldDuplicates => Volatile.Read(ref _held);

    public ILogger CreateLogger(string categoryName) => new Counter(this, categoryName);

    public void Dispose()
    {
    }

    private class Counter(HoldingLoggerProvider parent, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => category.Contains("NativeAckReceiver");

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (category.Contains("NativeAckReceiver") && formatter(state, exception).StartsWith("Holding duplicate delivery"))
            {
                Interlocked.Increment(ref parent._held);
            }
        }
    }
}
