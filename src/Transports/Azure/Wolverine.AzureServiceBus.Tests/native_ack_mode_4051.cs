using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

#region messages and handler

public record AsbNativeAckWork(int Number);

/// <summary>
/// Identifies WHICH host handled a message. Registered as a singleton per host and injected into the handler.
/// Without it these tests are vacuous: the tracking bag is static and process-wide, so a handler parked inside a
/// host that has since been disposed still resumes in this process and still records its message. A "the dead
/// node's messages came back" assertion written over untagged numbers is therefore satisfied by the DEAD node
/// finishing its own work, and passes just as happily under BufferedInMemory -- which is exactly what it is
/// supposed to rule out. (Confirmed the hard way: the first version of this file passed its own red baseline.)
/// </summary>
public class AsbNodeTag(string name)
{
    public string Name { get; } = name;
}

public static class AsbNativeAckTracking
{
    /// <summary>Every (node, number) whose handler RAN TO COMPLETION -- one entry per completion.</summary>
    public static readonly ConcurrentBag<(string Node, int Number)> Handled = new();

    /// <summary>
    /// Every (node, number) whose handler was ENTERED, one entry per entry. A redelivery shows up here even while
    /// the original invocation is still parked and has therefore recorded nothing in <see cref="Handled" />, which
    /// is what lets the lease renewal test see a redelivery as it happens.
    /// </summary>
    public static readonly ConcurrentBag<(string Node, int Number)> Entered = new();

    /// <summary>When set, handlers park here forever -- standing in for a node that dies mid-flight.</summary>
    public static TaskCompletionSource? Block;

    public static void Reset()
    {
        Handled.Clear();
        Entered.Clear();
        Block = null;
    }

    /// <summary>Distinct numbers handled by anyone.</summary>
    public static IEnumerable<int> HandledInRange(int start, int count)
    {
        return Handled.Select(x => x.Number).Where(x => x >= start && x < start + count).Distinct().OrderBy(x => x);
    }

    /// <summary>Distinct numbers handled by THIS node specifically.</summary>
    public static IEnumerable<int> HandledBy(string node, int start, int count)
    {
        return Handled.Where(x => x.Node == node && x.Number >= start && x.Number < start + count)
            .Select(x => x.Number).Distinct().OrderBy(x => x);
    }

    /// <summary>How many times a handler was entered for this number, across every node.</summary>
    public static int EnteredCount(int number)
    {
        return Entered.Count(x => x.Number == number);
    }

    /// <summary>Total handler completions in this range, counting repeats -- a redelivery makes this exceed the range.</summary>
    public static int TotalCompletionsInRange(int start, int count)
    {
        return Handled.Count(x => x.Number >= start && x.Number < start + count);
    }
}

public class AsbNativeAckWorkHandler
{
    public async Task Handle(AsbNativeAckWork message, AsbNodeTag node)
    {
        AsbNativeAckTracking.Entered.Add((node.Name, message.Number));

        if (AsbNativeAckTracking.Block != null)
        {
            await AsbNativeAckTracking.Block.Task;
        }

        AsbNativeAckTracking.Handled.Add((node.Name, message.Number));
    }
}

#endregion

/// <summary>
/// GH-4051. Azure Service Bus's adoption of <see cref="EndpointMode.NativeAck" />. The guarantee under test is the
/// one the mode exists for -- Buffered's throughput with Inline's no-loss behaviour -- plus the Azure Service Bus
/// specific half of it, which is that the broker's message lock survives the whole time an envelope is queued
/// (see native_ack_lease_renewal_4051).
/// </summary>
/// <remarks>
/// Every test gets its OWN queue. These share one static tracking bag and one emulator, so a shared queue lets the
/// redelivery test's messages land in another test's assertion.
/// </remarks>
public class native_ack_mode_4051 : IAsyncLifetime
{
    private const string ModeQueue = "native-ack-4051-mode";
    private const string EndToEndQueue = "native-ack-4051-e2e";
    private const string RedeliveryQueue = "native-ack-4051-redelivery";
    private const string SubscriptionTopic = "native-ack-4051-topic";
    private const string SubscriptionName = "native-ack-4051-sub";

    public ValueTask InitializeAsync()
    {
        AsbNativeAckTracking.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        AsbNativeAckTracking.Block?.TrySetResult();
        AsbNativeAckTracking.Reset();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A short lock duration keeps the redelivery test honest without making it slow: once the first node is gone
    /// nothing is renewing these locks, so this is how long the broker waits before handing the messages to the
    /// replacement node.
    /// </summary>
    internal static Task<IHost> startQueueHostAsync(string queueName, string node = "only")
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting();

                opts.Services.AddSingleton(new AsbNodeTag(node));
                opts.Discovery.DisableConventionalDiscovery().IncludeType<AsbNativeAckWorkHandler>();

                opts.ListenToAzureServiceBusQueue(queueName)
                    .Named(queueName)
                    .ConfigureQueue(q => q.LockDuration = TimeSpan.FromSeconds(10))
                    .ProcessInParallelWithNativeAcks();

                opts.PublishMessage<AsbNativeAckWork>().ToAzureServiceBusQueue(queueName);
            }).StartAsync();
    }

    [Fact]
    public async Task the_endpoint_really_is_in_native_ack_mode()
    {
        using var host = await startQueueHostAsync(ModeQueue);
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        var endpoint = runtime.Endpoints.EndpointByName(ModeQueue).ShouldNotBeNull();
        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);

        // Nothing is acked until the handler succeeds, so the unacked window is the back pressure
        endpoint.ShouldEnforceBackPressure().ShouldBeFalse();

        // GH-4048. An Azure Service Bus delivery expires on its own, so this endpoint has to declare the clock --
        // and because it does, ListeningAgent refuses to start it at all unless the listener it built can renew.
        // That this host started is therefore itself part of the assertion.
        var queue = endpoint.ShouldBeOfType<AzureServiceBusQueue>();
        queue.holdsExpiringLease.ShouldBeTrue();
        queue.LockDuration.ShouldBe(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// End to end, and specifically that handler completion really does SETTLE the broker delivery. The quiet
    /// period after the last handler is longer than the queue's lock duration, so a delivery that was handled but
    /// never completed would come back inside it and show up as an eleventh completion.
    /// </summary>
    [Fact]
    public async Task messages_are_processed_end_to_end_and_settled_exactly_once()
    {
        using var host = await startQueueHostAsync(EndToEndQueue);
        var bus = host.MessageBus();

        for (var i = 0; i < 10; i++)
        {
            await bus.SendAsync(new AsbNativeAckWork(i));
        }

        await waitForAsync(() => AsbNativeAckTracking.HandledInRange(0, 10).Count() >= 10, TimeSpan.FromSeconds(60));

        AsbNativeAckTracking.HandledInRange(0, 10).ShouldBe(Enumerable.Range(0, 10));

        // Longer than the 10 second lock duration these queues are configured with
        await Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        AsbNativeAckTracking.TotalCompletionsInRange(0, 10)
            .ShouldBe(10, "a delivery was redelivered after its handler finished, so completion did not settle it");
    }

    /// <summary>
    /// The whole point of the mode. Under BufferedInMemory these messages are completed the instant they arrive, so
    /// a node that dies before the handler finishes loses every one of them. Under NativeAck nothing is completed
    /// until the handler succeeds, so the broker's lock expires and it hands them all to the next node.
    /// </summary>
    [Fact]
    public async Task nothing_is_acked_until_the_handler_succeeds_so_a_draining_node_loses_nothing()
    {
        AsbNativeAckTracking.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstHost = await startQueueHostAsync(RedeliveryQueue, "first");
        var bus = firstHost.MessageBus();

        for (var i = 0; i < 5; i++)
        {
            await bus.SendAsync(new AsbNativeAckWork(100 + i));
        }

        // Let the deliveries actually reach the parked handlers before killing the node
        await waitForAsync(() => AsbNativeAckTracking.Entered.Count(x => x.Node == "first" && x.Number >= 100) >= 5,
            TimeSpan.FromSeconds(60));

        // The node shuts down mid-flight, having acked nothing. GH-4095: Dispose() DRAINS --
        // WolverineRuntime.DisposeAsync calls StopAsync -- so this is a tidy shutdown, not a crash
        firstHost.Dispose();

        AsbNativeAckTracking.HandledInRange(100, 5).ShouldBeEmpty();

        // Releasing the park lets the DEAD node's own handlers run to completion inside this process. That is a
        // red herring by construction and the assertion below deliberately ignores it: what has to be proved is
        // that the broker handed these deliveries to a DIFFERENT node, which only happens if nothing acked them.
        AsbNativeAckTracking.Block!.TrySetResult();
        AsbNativeAckTracking.Block = null;

        using var secondHost = await startQueueHostAsync(RedeliveryQueue, "second");

        await waitForAsync(() => AsbNativeAckTracking.HandledBy("second", 100, 5).Count() >= 5,
            TimeSpan.FromSeconds(90));

        AsbNativeAckTracking.HandledBy("second", 100, 5).ShouldBe(Enumerable.Range(100, 5));
    }

    /// <summary>
    /// Subscriptions opt into the mode separately from queues (deliberately -- a topic shares their base class and
    /// must never pick native acks up by inheritance), so the claim is tested separately too.
    /// </summary>
    [Fact]
    public async Task subscriptions_work_in_native_ack_mode_too()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting();

                opts.Services.AddSingleton(new AsbNodeTag("subscriber"));
                opts.Discovery.DisableConventionalDiscovery().IncludeType<AsbNativeAckWorkHandler>();

                opts.ListenToAzureServiceBusSubscription(SubscriptionName)
                    .FromTopic(SubscriptionTopic)
                    .Named(SubscriptionName)
                    .ProcessInParallelWithNativeAcks();

                opts.PublishMessage<AsbNativeAckWork>().ToAzureServiceBusTopic(SubscriptionTopic);
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var endpoint = runtime.Endpoints.EndpointByName(SubscriptionName).ShouldNotBeNull();
        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);
        endpoint.ShouldBeOfType<AzureServiceBusSubscription>().holdsExpiringLease.ShouldBeTrue();

        var bus = host.MessageBus();
        for (var i = 200; i < 205; i++)
        {
            await bus.SendAsync(new AsbNativeAckWork(i));
        }

        await waitForAsync(() => AsbNativeAckTracking.HandledInRange(200, 5).Count() >= 5, TimeSpan.FromSeconds(60));

        AsbNativeAckTracking.HandledInRange(200, 5).ShouldBe(Enumerable.Range(200, 5));
    }

    internal static async Task waitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
