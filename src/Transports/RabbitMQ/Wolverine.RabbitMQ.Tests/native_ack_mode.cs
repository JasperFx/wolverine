using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

#region messages and handler

public record NativeAckWork(int Number);

public static class NativeAckWorkTracking
{
    /// <summary>
    /// GH-4050. Records (node, message number), NOT just the number. Attribution is load-bearing for the
    /// dead-node test: the Block gate below is static and process-wide, so releasing it unparks the DEAD host's
    /// handlers too. They then complete in-process and record. A bag of bare numbers is therefore satisfied by
    /// the dead node's own tasks unwinding, whether or not the broker redelivered anything to the new node --
    /// which is precisely the thing the test exists to prove.
    /// </summary>
    public static readonly ConcurrentBag<(string Node, int Number)> Handled = new();

    /// <summary>When set, handlers park here forever -- standing in for a node that dies mid-flight.</summary>
    public static TaskCompletionSource? Block;

    /// <summary>
    /// GH-4095. Recorded BEFORE the handler parks on <see cref="Block" />, so a test can wait until deliveries
    /// have genuinely reached handlers before killing the node. The obvious signal -- the listener circuit's
    /// QueueCount -- is always 0 under NativeAck, because NativeAckReceiver holds the delivery unacknowledged
    /// rather than queueing it. Waiting on that is waiting on something that never happens.
    /// </summary>
    public static readonly ConcurrentBag<(string Node, int Number)> Started = new();

    public static void Reset()
    {
        Handled.Clear();
        Started.Clear();
        Block = null;
    }
}

public class NativeAckWorkHandler
{
    public async Task Handle(NativeAckWork message, IWolverineRuntime runtime)
    {
        NativeAckWorkTracking.Started.Add((runtime.Options.ServiceName, message.Number));

        if (NativeAckWorkTracking.Block != null)
        {
            await NativeAckWorkTracking.Block.Task;
        }

        NativeAckWorkTracking.Handled.Add((runtime.Options.ServiceName, message.Number));
    }
}

#endregion

/// <summary>
/// GH-3708. RabbitMQ is the first transport to opt into EndpointMode.NativeAck. The guarantee under test is the
/// one the mode exists for: Buffered's throughput with Inline's no-loss behaviour.
/// </summary>
public class native_ack_mode : IAsyncLifetime
{
    // A queue per test. These share one static tracking bag and one broker, so a single queue let the
    // redelivery test's messages land in another test's assertion.
    private const string ModeQueue = "native-ack-3708-mode";
    private const string EndToEndQueue = "native-ack-3708-e2e";
    private const string RedeliveryQueue = "native-ack-3708-redelivery";
    private const string HardKillQueue = "native-ack-4095-hard-kill";

    public ValueTask InitializeAsync()
    {
        NativeAckWorkTracking.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        NativeAckWorkTracking.Block?.TrySetResult();
        NativeAckWorkTracking.Reset();
        return ValueTask.CompletedTask;
    }

    private static Task<IHost> startHostAsync(string queueName, string nodeName = "node")
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = nodeName;

                // GH-4095. Named per node so the broker can be asked to drop exactly this host's
                // connections -- that is what makes the hard kill test an actual kill
                opts.UseRabbitMq(factory =>
                {
                    factory.HostName = "localhost";
                    factory.Port = 5672;
                    factory.ClientProvidedName = nodeName;
                }).AutoProvision();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<NativeAckWorkHandler>();

                opts.ListenToRabbitQueue(queueName)
                    .Named(queueName)
                    .ProcessInParallelWithNativeAcks();

                opts.PublishMessage<NativeAckWork>().ToRabbitQueue(queueName);
            }).StartAsync();
    }

    [Fact]
    public async Task the_endpoint_really_is_in_native_ack_mode_with_a_native_ack_receiver()
    {
        using var host = await startHostAsync(ModeQueue);
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        var endpoint = runtime.Endpoints.EndpointByName(ModeQueue).ShouldNotBeNull();
        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);

        // Prefetch IS the back pressure for this mode, so it must cover every lane that can be busy
        endpoint.ShouldEnforceBackPressure().ShouldBeFalse();
    }

    [Fact]
    public async Task messages_are_processed_end_to_end()
    {
        using var host = await startHostAsync(EndToEndQueue);
        var bus = host.MessageBus();

        for (var i = 0; i < 10; i++)
        {
            await bus.SendAsync(new NativeAckWork(i));
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (handledInRange(0, 10).Count() < 10 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        handledInRange(0, 10).ShouldBe(Enumerable.Range(0, 10));
    }

    /// <summary>
    /// The whole point of the mode. Under BufferedInMemory these messages are acked the instant they arrive, so
    /// a node that stops before the handler finishes loses every one of them. Under NativeAck nothing is acked
    /// until the handler succeeds, so the deliveries go back to the broker instead.
    /// </summary>
    /// <remarks>
    /// GH-4095. This is the GRACEFUL half: <c>host.Dispose()</c> drains, because
    /// <c>WolverineRuntime.DisposeAsync</c> calls <c>StopAsync</c> when the runtime has not already stopped.
    /// What it establishes is that nothing is acknowledged ahead of its handler, so work parked in a lane at
    /// shutdown comes back rather than vanishing. It says nothing about a crash -- see
    /// <see cref="a_hard_killed_node_loses_nothing_when_the_broker_drops_its_connection" /> for that, which has
    /// different guarantees: a drain produces no duplicates, an abrupt loss can.
    /// </remarks>
    [Fact]
    public async Task nothing_is_acked_until_the_handler_succeeds_so_a_draining_node_loses_nothing()
    {
        NativeAckWorkTracking.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstHost = await startHostAsync(RedeliveryQueue, "dead-node");
        var bus = firstHost.MessageBus();

        for (var i = 0; i < 5; i++)
        {
            await bus.SendAsync(new NativeAckWork(100 + i));
        }

        // Let the deliveries actually reach the parked handlers before stopping the node. GH-4095: this used
        // to wait on the listener circuit's QueueCount, which is ALWAYS 0 under NativeAck -- the receiver holds
        // the delivery unacknowledged rather than queueing it -- so it burned the full 15s every run and then
        // proceeded regardless. Gate on the handlers actually starting instead
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (startedByNode("dead-node", 100, 5).Count() == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        startedByNode("dead-node", 100, 5).Count().ShouldBeGreaterThan(0);

        // The node shuts down mid-flight, having acked nothing. GH-4095: Dispose() DRAINS --
        // WolverineRuntime.DisposeAsync calls StopAsync -- so this is a tidy shutdown, not a crash
        firstHost.Dispose();

        handledInRange(100, 5).ShouldBeEmpty();

        // A fresh node picks up every redelivered message
        NativeAckWorkTracking.Block!.TrySetResult();
        NativeAckWorkTracking.Block = null;

        using var secondHost = await startHostAsync(RedeliveryQueue, "fresh-node");

        deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (handledByNode("fresh-node", 100, 5).Count() < 5 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        // Asserted against the FRESH node specifically. Counting bare message numbers here would be satisfied by
        // the dead node's own parked handlers unwinding when the static gate was released above -- which proves
        // nothing about redelivery, the single thing this test exists to establish.
        handledByNode("fresh-node", 100, 5).ShouldBe(Enumerable.Range(100, 5));
    }

    /// <summary>
    /// GH-4095. The ABRUPT half. Disposing the host is a tidy shutdown, so every "dead node" test in these
    /// suites was really measuring a drain. Here the broker drops the node's connection underneath it: nothing
    /// gets to finish, no ack goes out, and every unacknowledged delivery on that connection is requeued
    /// immediately. That is the scenario the docs lean on when they say a dying node loses nothing, and until
    /// now it was only covered by GH-3713's chaos suite in SlowTests, which does not run on the PR path.
    /// </summary>
    [Fact]
    public async Task a_hard_killed_node_loses_nothing_when_the_broker_drops_its_connection()
    {
        using var probe = new RabbitManagementProbe();
        if (!await probe.IsAvailableAsync(TestContext.Current.CancellationToken))
        {
            // The management plugin is part of the rabbitmq:4-management image the repo's compose file pins,
            // so this should not happen in CI. Failing loudly beats silently not testing the thing
            throw new InvalidOperationException(
                "The RabbitMQ management API is not reachable on localhost:15672, so a hard kill cannot be performed.");
        }

        NativeAckWorkTracking.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstHost = await startHostAsync(HardKillQueue, "killed-node");
        var bus = firstHost.MessageBus();

        for (var i = 0; i < 5; i++)
        {
            await bus.SendAsync(new NativeAckWork(200 + i));
        }

        // Wait until the deliveries have actually reached the parked handlers. Killing an idle node proves
        // nothing -- there has to be an unacknowledged window for the broker to requeue. Gate on the event
        // itself rather than racing it: QueueCount is always 0 in this mode, so waiting on it just burns the
        // timeout and then kills an idle node
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (startedByNode("killed-node", 200, 5).Count() == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        startedByNode("killed-node", 200, 5).Count().ShouldBeGreaterThan(0);

        // The kill. NOT host.Dispose() -- the broker severs the connection, so the listener never drains
        (await probe.WaitForConnectionAsync("killed-node", 30.Seconds(), TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        var closed = await probe.ForceCloseConnectionsAsync("killed-node", TestContext.Current.CancellationToken);
        closed.ShouldBeGreaterThan(0);

        handledInRange(200, 5).ShouldBeEmpty();

        // Release the gate only AFTER the connection is gone, so the killed node's handlers complete into a
        // channel that no longer exists -- their acks cannot land, which is the real crash shape
        NativeAckWorkTracking.Block!.TrySetResult();
        NativeAckWorkTracking.Block = null;

        using var secondHost = await startHostAsync(HardKillQueue, "survivor-node");

        deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (handledByNode("survivor-node", 200, 5).Count() < 5 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        // Attribution matters for exactly the reason the graceful test documents: the gate is process-wide, so
        // the killed host's own handlers unwind and record. Counting bare numbers would be satisfied by that
        // and would prove nothing about redelivery
        handledByNode("survivor-node", 200, 5).ShouldBe(Enumerable.Range(200, 5));

        // Deliberately NOT asserting zero duplicates. GH-3713 measured that an abrupt loss produces up to one
        // duplicate per in-flight lane, because a handler can finish after its ack path is gone. No loss is the
        // guarantee; exactly-once is not
        firstHost.Dispose();
    }

    /// <summary>Messages in the range whose handler STARTED on the named node, gate or no gate.</summary>
    private static IEnumerable<int> startedByNode(string node, int start, int count)
    {
        return NativeAckWorkTracking.Started
            .Where(x => x.Node == node && x.Number >= start && x.Number < start + count)
            .Select(x => x.Number)
            .Distinct()
            .OrderBy(x => x);
    }

    private static IEnumerable<int> handledInRange(int start, int count)
    {
        return NativeAckWorkTracking.Handled
            .Select(x => x.Number)
            .Where(x => x >= start && x < start + count)
            .Distinct()
            .OrderBy(x => x);
    }

    /// <summary>Messages in the range handled specifically BY the named node.</summary>
    private static IEnumerable<int> handledByNode(string node, int start, int count)
    {
        return NativeAckWorkTracking.Handled
            .Where(x => x.Node == node && x.Number >= start && x.Number < start + count)
            .Select(x => x.Number)
            .Distinct()
            .OrderBy(x => x);
    }

}
