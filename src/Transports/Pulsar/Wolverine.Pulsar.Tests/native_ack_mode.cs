using System.Collections.Concurrent;
using DotPulsar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pulsar.Tests;

#region messages and handler

public record PulsarNativeAckWork(string Batch, int Number);

/// <summary>
/// Every piece of state here is keyed by a per-test batch id. These tests share one broker and one static
/// class, and the RabbitMQ suite this mirrors had to learn the hard way that a single shared queue plus
/// shared tracking lets one test's redelivered messages satisfy another test's assertion.
/// </summary>
public static class PulsarNativeAckTracking
{
    private static readonly ConcurrentDictionary<string, ConcurrentBag<int>> _handled = new();
    private static readonly ConcurrentDictionary<string, ConcurrentBag<int>> _started = new();
    private static readonly ConcurrentDictionary<string, byte> _parked = new();

    /// <summary>
    /// Cancelling this releases parked handlers into an exception rather than into a completion, so a delivery
    /// that was in flight on the node that "died" can never record itself as handled afterwards. That matters:
    /// if parked handlers were released normally, the redelivery assertion below would pass on the strength of
    /// the dead node's own tasks finishing, with no redelivery involved at all.
    /// </summary>
    public static CancellationTokenSource Released { get; private set; } = new();

    public static void Reset()
    {
        Released = new CancellationTokenSource();
    }

    public static void ParkHandlersFor(string batch)
    {
        _parked[batch] = 1;
    }

    public static void StopParking(string batch)
    {
        _parked.TryRemove(batch, out _);
    }

    public static bool IsParked(string batch)
    {
        return _parked.ContainsKey(batch);
    }

    public static ConcurrentBag<int> Handled(string batch)
    {
        return _handled.GetOrAdd(batch, _ => new ConcurrentBag<int>());
    }

    /// <summary>
    /// Deliveries whose handler has actually STARTED. The receiver's QueueCount is no use for "has the broker
    /// delivered these yet": the execution block dequeues all of them immediately and they park inside the
    /// handler, so the queue reads zero the whole time.
    /// </summary>
    public static ConcurrentBag<int> Started(string batch)
    {
        return _started.GetOrAdd(batch, _ => new ConcurrentBag<int>());
    }

    public static IEnumerable<int> HandledInOrder(string batch)
    {
        return Handled(batch).Distinct().OrderBy(x => x);
    }
}

public class PulsarNativeAckWorkHandler
{
    public async Task Handle(PulsarNativeAckWork message)
    {
        PulsarNativeAckTracking.Started(message.Batch).Add(message.Number);

        if (PulsarNativeAckTracking.IsParked(message.Batch))
        {
            // Stands in for a node that dies mid-flight: the handler never terminates, so nothing is ever acked.
            await Task.Delay(Timeout.InfiniteTimeSpan, PulsarNativeAckTracking.Released.Token);
        }

        PulsarNativeAckTracking.Handled(message.Batch).Add(message.Number);
    }
}

#endregion

/// <summary>
/// GH-4047. Pulsar's adoption of EndpointMode.NativeAck. Two things are under test: the guarantee the mode
/// exists for (Buffered's throughput with Inline's no-loss behaviour), and the correctness trap that made this
/// adoption more than a one-line opt-in -- Pulsar's ack strategy is configurable, and cumulative acking under
/// this mode is silent message loss.
/// </summary>
[Collection("pulsar")]
public class native_ack_mode : IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        PulsarNativeAckTracking.Reset();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await PulsarNativeAckTracking.Released.CancelAsync();
    }

    // A topic AND subscription per test, never a shared one.
    private static string uniqueTopic(string prefix)
    {
        return $"persistent://public/default/{prefix}-{Guid.NewGuid():N}";
    }

    private static Task<IHost> startHostAsync(string topic, string subscription,
        Action<PulsarListenerConfiguration>? configure = null)
    {
        return WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));

            opts.Discovery.DisableConventionalDiscovery().IncludeType<PulsarNativeAckWorkHandler>();

            var listener = opts.ListenToPulsarTopic(topic)
                .Named("native-ack-listener")
                .SubscriptionName(subscription)
                // Earliest so a send that races subscription creation is still delivered -- otherwise the test
                // would be measuring a startup race rather than the ack semantics.
                .SubscriptionInitialPosition(SubscriptionInitialPosition.Earliest)
                // The redelivery test kills a node and starts another on the same subscription. Unsubscribing on
                // close would delete the cursor along with every unacked delivery on it.
                .UnsubscribeOnClose(false)
                .ProcessInParallelWithNativeAcks();

            configure?.Invoke(listener);

            // Deliberately NOT SendInline(): the listener and the publisher resolve to the same PulsarEndpoint for
            // this topic, and Mode is one property governing both directions -- so SendInline() would quietly
            // reset the endpoint from NativeAck back to Inline and this whole class would test nothing.
            opts.PublishMessage<PulsarNativeAckWork>().ToPulsarTopic(topic);
        });
    }

    // ---- mode plumbing ----

    [Fact]
    public async Task the_endpoint_really_is_in_native_ack_mode()
    {
        var topic = uniqueTopic("nativeack-mode");
        using var host = await startHostAsync(topic, "sub-" + Guid.NewGuid().ToString("N"));

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var endpoint = runtime.Endpoints.EndpointByName("native-ack-listener").ShouldNotBeNull();

        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);

        // The broker's delivery window is the back pressure for this mode, so no BackPressureAgent
        endpoint.ShouldEnforceBackPressure().ShouldBeFalse();
    }

    [Fact]
    public void a_pulsar_endpoint_opts_into_native_ack()
    {
        var endpoint = endpointFor();
        endpoint.SupportsMode(EndpointMode.NativeAck).ShouldBeTrue();

        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);
    }

    // ---- receiver queue size (the prefetch equivalent) ----

    [Fact]
    public void receiver_queue_size_is_left_to_dotpulsar_outside_native_ack()
    {
        var endpoint = endpointFor();

        endpoint.Mode = EndpointMode.BufferedInMemory;
        endpoint.EffectiveReceiverQueueSize.ShouldBeNull();

        endpoint.Mode = EndpointMode.Durable;
        endpoint.EffectiveReceiverQueueSize.ShouldBeNull();
    }

    [Fact]
    public void native_ack_sizes_the_receiver_queue_to_the_lanes_that_can_be_busy()
    {
        var endpoint = endpointFor();
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.MaxDegreeOfParallelism = 4;

        endpoint.EffectiveReceiverQueueSize.ShouldBe(8u);

        // Group partitioning is the other lane count, and the bigger of the two has to win -- a slot count
        // above MaximumParallelMessages still means that many lanes can be occupied at once.
        endpoint.GroupShardingSlotNumber = PartitionSlots.Nine;
        endpoint.EffectiveReceiverQueueSize.ShouldBe(18u);

        endpoint.GroupShardingSlotNumber = PartitionSlots.Three;
        endpoint.EffectiveReceiverQueueSize.ShouldBe(8u);
    }

    [Fact]
    public void an_explicit_receiver_queue_size_beats_the_mode_default()
    {
        var endpoint = endpointFor();
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.MaxDegreeOfParallelism = 8;
        endpoint.ReceiverQueueSize = 500;

        endpoint.EffectiveReceiverQueueSize.ShouldBe(500u);
    }

    // ---- the correctness trap ----

    /// <summary>
    /// THE test this issue exists for. A cumulative ack settles every message up to a point in the subscription,
    /// and this mode completes messages in handler-completion order -- so the combination silently settles
    /// in-flight deliveries. It must be impossible to configure it and have it merely misbehave.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void cumulative_acks_cannot_silently_coexist_with_native_ack(bool nativeAckFirst)
    {
        var endpoint = endpointFor();

        // BOTH orderings, because these are applied as delayed configuration in the order the fluent calls were
        // written. A guard in the Mode setter would only catch one of them, which is the whole reason this is
        // validated after Compile().
        if (nativeAckFirst)
        {
            endpoint.Mode = EndpointMode.NativeAck;
            endpoint.AckStrategy = PulsarAckStrategy.Cumulative;
        }
        else
        {
            endpoint.AckStrategy = PulsarAckStrategy.Cumulative;
            endpoint.Mode = EndpointMode.NativeAck;
        }

        var problems = ListenerConfigurationValidator.Validate(endpoint).ToArray();

        var problem = problems.ShouldHaveSingleItem();
        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Fatal);

        // Names BOTH settings, so the message says what to change rather than only what is wrong
        problem.Message.ShouldContain("AcknowledgeCumulative");
        problem.Message.ShouldContain(nameof(EndpointMode.NativeAck));
    }

    [Fact]
    public void individual_and_batched_acks_are_both_fine_with_native_ack()
    {
        foreach (var strategy in new[] { PulsarAckStrategy.Individual, PulsarAckStrategy.Batched })
        {
            var endpoint = endpointFor();
            endpoint.Mode = EndpointMode.NativeAck;
            endpoint.AckStrategy = strategy;

            ListenerConfigurationValidator.Validate(endpoint).ShouldBeEmpty();
        }
    }

    /// <summary>
    /// The related refusal: a hot-tail listener reads through a non-durable Pulsar Reader with no cursor, so
    /// nothing is ever acknowledged and nothing is ever redelivered. The mode's guarantee cannot be made there.
    /// </summary>
    [Fact]
    public void hot_tail_listening_cannot_coexist_with_native_ack()
    {
        var endpoint = endpointFor();
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.IsHotTail = true;

        var problem = ListenerConfigurationValidator.Validate(endpoint).ShouldHaveSingleItem();
        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Fatal);
        problem.Message.ShouldContain("TailFromLatest");
    }

    [Fact]
    public async Task cumulative_acks_plus_native_ack_refuses_to_bootstrap()
    {
        var topic = uniqueTopic("nativeack-cumulative");

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var _ = await startHostAsync(topic, "sub-" + Guid.NewGuid().ToString("N"),
                c => c.AcknowledgeCumulative());
        });

        ex.ToString().ShouldContain("AcknowledgeCumulative");
        ex.ToString().ShouldContain(nameof(EndpointMode.NativeAck));
    }

    // ---- end to end ----

    [Fact]
    public async Task messages_are_processed_end_to_end()
    {
        var batch = Guid.NewGuid().ToString("N");
        var topic = uniqueTopic("nativeack-e2e");

        using var host = await startHostAsync(topic, "sub-" + Guid.NewGuid().ToString("N"));

        for (var i = 0; i < 10; i++)
        {
            await host.SendAsync(new PulsarNativeAckWork(batch, i));
        }

        await waitForAsync(batch, 10);

        PulsarNativeAckTracking.HandledInOrder(batch).ShouldBe(Enumerable.Range(0, 10));
    }

    /// <summary>
    /// The whole point of the mode. Under BufferedInMemory these deliveries are acked the instant they arrive, so
    /// a node that dies before the handlers finish loses every one of them. Under NativeAck nothing is acked until
    /// the handler succeeds, so the broker hands them all back when the consumer drops.
    /// </summary>
    [Fact]
    public async Task nothing_is_acked_until_the_handler_succeeds_so_a_draining_node_loses_nothing()
    {
        var batch = Guid.NewGuid().ToString("N");
        var topic = uniqueTopic("nativeack-redelivery");
        var subscription = "sub-" + Guid.NewGuid().ToString("N");

        PulsarNativeAckTracking.ParkHandlersFor(batch);

        // GH-4047 item 4. UseNativeRedelivery() makes a defer "leave it unacknowledged and ask Pulsar to
        // redeliver it"; the default is ack-then-republish, which under shutdown would settle a delivery that
        // is about to be re-sent by a process on its way out. Both survive here, but only one of them has no
        // window at all -- and having no window is exactly what this mode is being asked to prove.
        var firstHost = await startHostAsync(topic, subscription, c => c.UseNativeRedelivery());

        for (var i = 0; i < 5; i++)
        {
            await firstHost.SendAsync(new PulsarNativeAckWork(batch, 100 + i));
        }

        // Every one of the five has to actually be in a parked handler before the node dies -- otherwise the test
        // could be proving nothing more than that an undelivered message is still on the topic.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (PulsarNativeAckTracking.Started(batch).Distinct().Count() < 5 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        PulsarNativeAckTracking.Started(batch).Distinct().OrderBy(x => x).ShouldBe(Enumerable.Range(100, 5));

        // Not one of them acked, because not one handler has terminated
        PulsarNativeAckTracking.HandledInOrder(batch).ShouldBeEmpty();

        // The node goes away mid-flight. The parked handlers it leaves behind never terminate, so they can
        // neither ack nor contribute to the count below -- every message that reaches the second host got
        // there because Pulsar still owned it.
        await firstHost.StopAsync(TestContext.Current.CancellationToken);
        firstHost.Dispose();

        PulsarNativeAckTracking.HandledInOrder(batch).ShouldBeEmpty();

        // A fresh node on the same subscription picks up every redelivered message
        PulsarNativeAckTracking.StopParking(batch);

        using var secondHost = await startHostAsync(topic, subscription, c => c.UseNativeRedelivery());

        await waitForAsync(batch, 5);

        PulsarNativeAckTracking.HandledInOrder(batch).ShouldBe(Enumerable.Range(100, 5));
    }

    // ---- helpers ----

    private static PulsarEndpoint endpointFor()
    {
        var endpoint = new PulsarTransport()[new Uri("pulsar://persistent/public/default/nativeack")];
        endpoint.IsListener = true;
        return endpoint;
    }

    private static async Task waitForAsync(string batch, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (PulsarNativeAckTracking.HandledInOrder(batch).Count() < count && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

}
