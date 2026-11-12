using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-5533. A Service Bus redelivery of a message that is still in flight, with the in-memory idempotency guard
/// on. Service Bus only redelivers an unsettled message once its lock is gone, so these tests stop renewing almost
/// at once (a lock renewal ceiling barely above the lock duration): the lock lapses while the handler is parked and
/// the broker hands the message straight back out -- with a NEW lock token -- to the same listener.
/// </summary>
/// <remarks>
/// The guard used to complete that redelivery as a duplicate. Its lock is the valid one, so the complete succeeded
/// and the message was gone from the queue while its first delivery was still being handled. It is now held
/// unsettled, its lock is the one renewed, and it is completed only once the original has been processed.
/// </remarks>
public class native_ack_inflight_redelivery_5533 : IAsyncLifetime
{
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

    private static Task<IHost> startHostAsync(string queueName, string node)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting();

                opts.Services.AddSingleton(new AsbNodeTag(node));
                opts.Discovery.DisableConventionalDiscovery().IncludeType<AsbNativeAckWorkHandler>();
                opts.Durability.DrainTimeout = 2.Seconds();

                opts.ListenToAzureServiceBusQueue(queueName)
                    .Named(queueName)
                    // The Service Bus minimum lock. The emulator's ceiling on delivery count, comfortably above the
                    // two to four deliveries each message sees here, so repeated lock expiries cannot dead letter a
                    // message mid-test
                    .ConfigureQueue(q =>
                    {
                        q.LockDuration = 5.Seconds();
                        q.MaxDeliveryCount = 10;
                    })
                    .ProcessInParallelWithNativeAcks()
                    .WithInMemoryIdempotency();

                // Stop renewing almost immediately, so the lock lapses while the original is still parked
                opts.AzureServiceBusTransport().Queues[queueName].MaximumLockRenewalDuration = 6.Seconds();

                opts.PublishMessage<AsbNativeAckWork>().ToAzureServiceBusQueue(queueName);
            }).StartAsync();
    }

    private static async Task<IHost> startWithInFlightRedeliveriesAsync(string queueName, string node, int start,
        int count)
    {
        AsbNativeAckTracking.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var host = await startHostAsync(queueName, node);
        var bus = host.MessageBus();
        for (var i = 0; i < count; i++)
        {
            await bus.SendAsync(new AsbNativeAckWork(start + i));
        }

        await native_ack_mode_4051.waitForAsync(
            () => AsbNativeAckTracking.Entered.Count(x => x.Node == node && x.Number >= start) >= count, 60.Seconds());

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var endpoint = runtime.Endpoints.EndpointByName(queueName).ShouldNotBeNull();
        var guard = (GenerationalIdempotencyGuard)endpoint.IdempotencyGuard.ShouldNotBeNull();

        await native_ack_mode_4051.waitForAsync(() => guard.DuplicatesSuppressed >= count, 60.Seconds());
        guard.DuplicatesSuppressed.ShouldBeGreaterThanOrEqualTo(count);

        // Held, never run a second time
        AsbNativeAckTracking.Entered.Count(x => x.Node == node && x.Number >= start && x.Number < start + count)
            .ShouldBe(count);

        return host;
    }

    [Fact]
    public async Task an_in_flight_redelivery_is_not_completed_so_a_draining_node_still_loses_nothing()
    {
        const string queueName = "native-ack-5533-drain";

        var firstHost = await startWithInFlightRedeliveriesAsync(queueName, "redelivered", 300, 3);

        firstHost.Dispose();
        AsbNativeAckTracking.HandledInRange(300, 3).ShouldBeEmpty();

        // Unpark the dead node's handlers only once it is gone; anything they record is attributed to it
        AsbNativeAckTracking.Block!.TrySetResult();
        AsbNativeAckTracking.Block = null;

        using var secondHost = await startHostAsync(queueName, "after");

        await native_ack_mode_4051.waitForAsync(() => AsbNativeAckTracking.HandledBy("after", 300, 3).Count() >= 3,
            90.Seconds());

        AsbNativeAckTracking.HandledBy("after", 300, 3).ShouldBe(Enumerable.Range(300, 3));
    }

    [Fact]
    public async Task an_in_flight_redelivery_is_completed_on_its_own_lock_once_the_original_succeeds()
    {
        const string queueName = "native-ack-5533-settle";

        var firstHost = await startWithInFlightRedeliveriesAsync(queueName, "settling", 400, 3);

        AsbNativeAckTracking.Block!.TrySetResult();
        AsbNativeAckTracking.Block = null;

        await native_ack_mode_4051.waitForAsync(() => AsbNativeAckTracking.HandledBy("settling", 400, 3).Count() >= 3,
            30.Seconds());
        AsbNativeAckTracking.TotalCompletionsInRange(400, 3).ShouldBe(3);

        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        firstHost.Dispose();

        // Anything left unsettled would come back to this node within one lock duration
        using var secondHost = await startHostAsync(queueName, "leftovers");
        await Task.Delay(10.Seconds(), TestContext.Current.CancellationToken);

        AsbNativeAckTracking.Entered.Count(x => x.Node == "leftovers").ShouldBe(0);
    }
}
