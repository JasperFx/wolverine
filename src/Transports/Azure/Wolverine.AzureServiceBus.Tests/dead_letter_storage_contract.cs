using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// Coverage for the transport-agnostic dead-letter-destination contract (GH-3104) as reported by
/// Azure Service Bus endpoints: queues and subscriptions report
/// <see cref="DeadLetterStorageMode.Native"/> by default (managed dead letter queue / native
/// sub-queue), while disabling dead lettering on a queue falls back to
/// <see cref="DeadLetterStorageMode.Durable"/>.
/// </summary>
public class dead_letter_storage_contract
{
    [Fact]
    public void queue_is_native_by_default()
    {
        var transport = new AzureServiceBusTransport();
        transport.Queues["orders"].DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Native);
    }

    [Fact]
    public void queue_is_durable_when_dead_lettering_disabled()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["orders"];
        queue.DeadLetterQueueName = null;

        queue.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Durable);
    }

    [Fact]
    public void subscription_is_native()
    {
        var transport = new AzureServiceBusTransport();
        var topic = transport.Topics["events"];
        var subscription = new AzureServiceBusSubscription(transport, topic, "orders");

        subscription.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Native);
    }

    // GH-5313. EnableDeadLetterQueueRecovery() promotes a listening endpoint to NativeWithRecovery when the
    // recovery listener drains the destination that endpoint dead-letters to.

    private static AzureServiceBusTransport transportWithRecovery(params string[] endpointNames)
    {
        var options = new WolverineOptions();
        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();
        var configuration = new AzureServiceBusConfiguration(transport, options);

        if (endpointNames.Length == 0)
        {
            configuration.EnableDeadLetterQueueRecovery();
        }
        else
        {
            configuration.EnableDeadLetterQueueRecovery(endpointNames);
        }

        return transport;
    }

    private static AzureServiceBusQueue listeningQueue(AzureServiceBusTransport transport, string name,
        EndpointMode mode = EndpointMode.BufferedInMemory)
    {
        var queue = transport.Queues[name];
        queue.IsListener = true;
        queue.Mode = mode;
        return queue;
    }

    private static AzureServiceBusSubscription listeningSubscription(AzureServiceBusTransport transport,
        string name)
    {
        var subscription = new AzureServiceBusSubscription(transport, transport.Topics["events"], name);
        subscription.IsListener = true;
        return subscription;
    }

    [Fact]
    public void listening_queue_is_native_with_recovery_when_recovery_covers_everything()
    {
        var transport = transportWithRecovery();

        listeningQueue(transport, "orders").DeadLetterStorage
            .ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
        listeningQueue(transport, "payments", EndpointMode.Inline).DeadLetterStorage
            .ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
        listeningQueue(transport, "shipments", EndpointMode.Durable).DeadLetterStorage
            .ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
    }

    [Fact]
    public void queue_that_is_not_listening_stays_native_because_recovery_only_drains_listeners()
    {
        var transport = transportWithRecovery();

        transport.Queues["outgoing"].DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Native);
    }

    [Fact]
    public void queue_with_dead_lettering_disabled_stays_durable_with_recovery_on()
    {
        var transport = transportWithRecovery();
        var queue = listeningQueue(transport, "orders");
        queue.DeadLetterQueueName = null;

        queue.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Durable);
    }

    [Fact]
    public void inline_queue_is_native_with_recovery_when_its_own_name_is_listed()
    {
        // An inline queue dead-letters to its own $DeadLetterQueue sub-queue, the source named after the queue
        var transport = transportWithRecovery("orders");

        listeningQueue(transport, "orders", EndpointMode.Inline).DeadLetterStorage
            .ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
        listeningQueue(transport, "payments", EndpointMode.Inline).DeadLetterStorage
            .ShouldBe(DeadLetterStorageMode.Native);
    }

    [Fact]
    public void buffered_queue_follows_its_managed_dead_letter_queue_when_names_are_listed()
    {
        // A buffered or durable queue dead-letters to its managed dead letter queue, so listing only the
        // queue's own name (its sub-queue) does not recover what Wolverine dead-letters there
        var onlyTheQueue = transportWithRecovery("orders");
        listeningQueue(onlyTheQueue, "orders").DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Native);

        var theManagedQueue = transportWithRecovery(AzureServiceBusTransport.DeadLetterQueueName);
        listeningQueue(theManagedQueue, "orders").DeadLetterStorage
            .ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
        listeningQueue(theManagedQueue, "shipments", EndpointMode.Durable).DeadLetterStorage
            .ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
    }

    [Fact]
    public void buffered_queue_with_its_own_dead_letter_queue_name()
    {
        var transport = transportWithRecovery("orders-dlq");

        var orders = listeningQueue(transport, "orders");
        orders.DeadLetterQueueName = "orders-dlq";
        orders.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.NativeWithRecovery);

        // Still on the default managed dead letter queue, which is not in the list
        listeningQueue(transport, "payments").DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Native);
    }

    [Fact]
    public void listed_names_are_compared_after_sanitizing()
    {
        var transport = transportWithRecovery("Orders");

        listeningQueue(transport, "orders", EndpointMode.Inline).DeadLetterStorage
            .ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
    }

    [Fact]
    public void listening_subscription_is_native_with_recovery_when_recovery_covers_everything()
    {
        var transport = transportWithRecovery();

        listeningSubscription(transport, "orders").DeadLetterStorage
            .ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
    }

    [Fact]
    public void subscription_is_native_with_recovery_only_when_listed()
    {
        var transport = transportWithRecovery("orders");

        listeningSubscription(transport, "orders").DeadLetterStorage
            .ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
        listeningSubscription(transport, "payments").DeadLetterStorage
            .ShouldBe(DeadLetterStorageMode.Native);
    }

    [Fact]
    public void subscription_that_is_not_listening_stays_native()
    {
        var transport = transportWithRecovery();
        var subscription = new AzureServiceBusSubscription(transport, transport.Topics["events"], "orders");

        subscription.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Native);
    }
}
