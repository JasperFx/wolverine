using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-4048/GH-4051. Azure Service Bus puts a clock on an <em>unsettled</em> delivery -- the entity's lock duration --
/// and a NativeAck lane holds a delivery for queue time plus handler time. Without renewal such an endpoint is a
/// duplicate generator by construction rather than merely at risk, so this is the load-bearing half of the adoption.
/// </summary>
public class native_ack_lease_renewal_4051
{
    private const string RenewalQueue = "native-ack-4051-lease";

    /// <summary>
    /// The one that matters. The handler parks for THREE lock durations, which without renewal is two guaranteed
    /// redeliveries: the emulator expires a lock exactly on its duration and hands the message straight back out
    /// (verified directly -- a complete after the lock duration fails with MessageLockLost and the message comes
    /// back with DeliveryCount 2). Counting handler ENTRIES rather than completions is what makes the redelivery
    /// visible while the original invocation is still parked.
    /// </summary>
    [Fact]
    public async Task a_parked_delivery_is_not_redelivered_while_its_lease_is_renewed()
    {
        AsbNativeAckTracking.Reset();
        AsbNativeAckTracking.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseAzureServiceBusTesting();

                    opts.Services.AddSingleton(new AsbNodeTag("renewing"));
                    opts.Discovery.DisableConventionalDiscovery().IncludeType<AsbNativeAckWorkHandler>();

                    opts.ListenToAzureServiceBusQueue(RenewalQueue)
                        .Named(RenewalQueue)
                        // The renewal tick is half of this, so 10 seconds means a tick every 5 -- short enough to
                        // keep the test quick, long enough that a slow emulator round trip cannot fake a pass.
                        .ConfigureQueue(q => q.LockDuration = TimeSpan.FromSeconds(10))
                        .ProcessInParallelWithNativeAcks();

                    opts.PublishMessage<AsbNativeAckWork>().ToAzureServiceBusQueue(RenewalQueue);
                }).StartAsync(TestContext.Current.CancellationToken);

            await host.MessageBus().SendAsync(new AsbNativeAckWork(300));

            await native_ack_mode_4051.waitForAsync(() => AsbNativeAckTracking.EnteredCount(300) >= 1,
                TimeSpan.FromSeconds(60));

            AsbNativeAckTracking.EnteredCount(300).ShouldBe(1);

            // Park across three full lock durations. Nothing here is probabilistic: an un-renewed lock expires on
            // the tenth second and the broker redelivers immediately.
            await Task.Delay(TimeSpan.FromSeconds(31), TestContext.Current.CancellationToken);

            AsbNativeAckTracking.EnteredCount(300)
                .ShouldBe(1, "the delivery was redelivered while it was still being handled, so its lock was not renewed");

            // ...and it still settles cleanly on the lock it has been holding all along
            AsbNativeAckTracking.Block!.TrySetResult();

            await native_ack_mode_4051.waitForAsync(() => AsbNativeAckTracking.HandledInRange(300, 1).Any(),
                TimeSpan.FromSeconds(30));

            AsbNativeAckTracking.HandledInRange(300, 1).ShouldBe([300]);
        }
        finally
        {
            AsbNativeAckTracking.Block?.TrySetResult();
            AsbNativeAckTracking.Reset();
        }
    }

    /// <summary>
    /// The contract's hard part: <c>RenewLeasesAsync</c> reports the subset it could NOT renew, and core drops those
    /// without settling them. Reporting a delivery that is actually fine would throw away a perfectly good message,
    /// so this drives one dead lock and one live one through the same call and pins that only the dead one comes
    /// back.
    /// </summary>
    [Fact]
    public async Task renew_leases_reports_only_the_deliveries_whose_lock_is_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        const string queueName = "native-ack-4051-lost-lock";

        var admin = new ServiceBusAdministrationClient(Servers.AzureServiceBusManagementConnectionString);
        if ((await admin.QueueExistsAsync(queueName, ct)).Value)
        {
            await admin.DeleteQueueAsync(queueName, ct);
        }

        await admin.CreateQueueAsync(new CreateQueueOptions(queueName) { LockDuration = TimeSpan.FromMinutes(5) },
            ct);

        await using var client = new ServiceBusClient(Servers.AzureServiceBusConnectionString);
        var sender = client.CreateSender(queueName);
        await sender.SendMessageAsync(new ServiceBusMessage("dead-lock"), ct);
        await sender.SendMessageAsync(new ServiceBusMessage("live-lock"), ct);

        var receiver = client.CreateReceiver(queueName,
            new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });

        // Take both deliveries BEFORE the listener exists, so its receive loop has nothing to compete for
        var messages = await receiver.ReceiveMessagesAsync(2, TimeSpan.FromSeconds(10), ct);
        messages.Count.ShouldBe(2);

        var deadMessage = messages[0];
        var liveMessage = messages[1];

        // Settling one of them is the cheapest way to produce a genuinely invalid lock -- the broker answers a
        // renewal on it with MessageLockLost, which is exactly what an expired lock looks like too.
        await receiver.CompleteMessageAsync(deadMessage, ct);

        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues[queueName];

        await using var listener = new BatchedAzureServiceBusListener(queue,
            NullLogger<BatchedAzureServiceBusListener>.Instance, Substitute.For<IReceiver>(), receiver,
            Substitute.For<IAzureServiceBusEnvelopeMapper>(), Substitute.For<ISender>());

        var dead = new AzureServiceBusEnvelope(deadMessage, receiver);
        var live = new AzureServiceBusEnvelope(liveMessage, receiver);

        var lost = await listener.RenewLeasesAsync([dead, live], ct);

        lost.ShouldHaveSingleItem().ShouldBeSameAs(dead);

        // The live one is not merely absent from the result -- its lock was really pushed out
        liveMessage.LockedUntil.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(4));

        await receiver.CompleteMessageAsync(liveMessage, ct);
        await admin.DeleteQueueAsync(queueName, ct);
    }

    [Fact]
    public void the_listener_advertises_the_endpoint_s_own_clock()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["lease-durations"];
        queue.Options.LockDuration = TimeSpan.FromSeconds(45);
        queue.MaximumLockRenewalDuration = TimeSpan.FromMinutes(20);

        queue.LockDuration.ShouldBe(TimeSpan.FromSeconds(45));
        ((ISupportLeaseRenewal)listenerFor(queue)).LeaseDuration.ShouldBe(TimeSpan.FromSeconds(45));
        ((ISupportLeaseRenewal)listenerFor(queue)).MaximumLeaseExtension.ShouldBe(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void queues_and_subscriptions_declare_an_expiring_lease_but_topics_do_not()
    {
        var transport = new AzureServiceBusTransport();

        transport.Queues["holds-a-lease"].holdsExpiringLease.ShouldBeTrue();

        var topic = transport.Topics["lease-topic"];
        new AzureServiceBusSubscription(transport, topic, "holds-a-lease").holdsExpiringLease.ShouldBeTrue();

        // A topic is only ever published to, so there is no unsettled delivery to expire
        topic.holdsExpiringLease.ShouldBeFalse();
    }

    [Fact]
    public void maximum_lock_renewal_duration_must_be_positive()
    {
        var queue = new AzureServiceBusTransport().Queues["bad-ceiling"];

        queue.MaximumLockRenewalDuration.ShouldBe(TimeSpan.FromHours(1));
        Should.Throw<ArgumentOutOfRangeException>(() => queue.MaximumLockRenewalDuration = TimeSpan.Zero);
        Should.Throw<ArgumentOutOfRangeException>(() => queue.MaximumLockRenewalDuration = TimeSpan.FromSeconds(-1));
    }

    /// <summary>
    /// Builds a listener over a client that is never connected to anything. Only the two duration properties are
    /// read, and neither of them touches the broker.
    /// </summary>
    private static BatchedAzureServiceBusListener listenerFor(AzureServiceBusEndpoint endpoint)
    {
        var client = new ServiceBusClient(Servers.AzureServiceBusConnectionString);
        return new BatchedAzureServiceBusListener(endpoint, NullLogger<BatchedAzureServiceBusListener>.Instance,
            Substitute.For<IReceiver>(), client.CreateReceiver("never-used"),
            Substitute.For<IAzureServiceBusEnvelopeMapper>(), Substitute.For<ISender>());
    }
}

/// <summary>
/// GH-4051. Prefetch is the one part of a NativeAck endpoint's backlog that lease renewal does not protect: an
/// Azure Service Bus message ages against its lock from the moment the CLIENT buffers it, and a prefetched message
/// has no Envelope yet, so nothing is tracking it. The mode default is therefore sized to the lanes rather than for
/// raw throughput -- and an explicit setting at either level always wins over it.
/// </summary>
public class native_ack_prefetch_defaults_4051
{
    private static AzureServiceBusQueue queueWith(EndpointMode mode, Action<AzureServiceBusTransport>? configureTransport = null)
    {
        var transport = new AzureServiceBusTransport();
        configureTransport?.Invoke(transport);

        var queue = transport.Queues["prefetch-" + mode];
        queue.Mode = mode;
        return queue;
    }

    [Fact]
    public void native_ack_sizes_prefetch_to_the_lanes()
    {
        var queue = queueWith(EndpointMode.NativeAck);
        queue.MaxDegreeOfParallelism = 7;

        queue.PrefetchCount.ShouldBe(14);
    }

    [Fact]
    public void a_group_partitioned_native_ack_endpoint_covers_every_slot()
    {
        var queue = queueWith(EndpointMode.NativeAck);
        queue.MaxDegreeOfParallelism = 2;
        queue.GroupShardingSlotNumber = (PartitionSlots)9;

        // Every slot is a lane that can be busy at once, so the slot count is the floor
        queue.PrefetchCount.ShouldBe(18);
    }

    [Fact]
    public void every_other_mode_keeps_the_shipping_default_of_zero()
    {
        queueWith(EndpointMode.BufferedInMemory).PrefetchCount.ShouldBe(0);
        queueWith(EndpointMode.Inline).PrefetchCount.ShouldBe(0);
        queueWith(EndpointMode.Durable).PrefetchCount.ShouldBe(0);
    }

    [Fact]
    public void an_explicit_endpoint_setting_wins()
    {
        var queue = queueWith(EndpointMode.NativeAck);
        queue.MaxDegreeOfParallelism = 7;
        queue.PrefetchCount = 3;

        queue.PrefetchCount.ShouldBe(3);
    }

    [Fact]
    public void an_explicit_transport_wide_setting_wins_too()
    {
        var queue = queueWith(EndpointMode.NativeAck, t => t.PrefetchCount = 5);
        queue.MaxDegreeOfParallelism = 7;

        queue.PrefetchCount.ShouldBe(5);
    }

    /// <summary>
    /// Zero is a legitimate transport-wide choice -- it is what turns prefetch off altogether -- so it has to be
    /// distinguishable from "nobody chose anything", which is the case the mode default is for.
    /// </summary>
    [Fact]
    public void an_explicit_transport_wide_zero_is_still_a_choice()
    {
        var queue = queueWith(EndpointMode.NativeAck, t => t.PrefetchCount = 0);
        queue.MaxDegreeOfParallelism = 7;

        queue.PrefetchCount.ShouldBe(0);
    }
}
