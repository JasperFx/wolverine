using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// Coverage for the transport-agnostic dead-letter-destination contract (GH-3104) as reported by SQS
/// queues: a configured native dead letter queue reports <see cref="DeadLetterStorageMode.Native"/>,
/// while disabling it (per-queue or globally) falls back to <see cref="DeadLetterStorageMode.Durable"/>.
/// </summary>
public class dead_letter_storage_contract
{
    [Fact]
    public void native_by_default()
    {
        var transport = new AmazonSqsTransport();
        transport.Queues["orders"].DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Native);
    }

    [Fact]
    public void durable_when_dead_lettering_disabled_for_the_queue()
    {
        var transport = new AmazonSqsTransport();
        var queue = transport.Queues["orders"];
        queue.DeadLetterQueueName = null;

        queue.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Durable);
    }

    [Fact]
    public void durable_when_all_native_dead_lettering_disabled()
    {
        var transport = new AmazonSqsTransport { DisableDeadLetterQueues = true };
        transport.Queues["orders"].DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Durable);
    }

    // GH-5313. EnableDeadLetterQueueRecovery() promotes a queue to NativeWithRecovery when the recovery
    // listener drains that queue's dead letter queue.

    private static AmazonSqsTransport transportWithRecovery(params string[] deadLetterQueueNames)
    {
        var options = new WolverineOptions();
        var transport = options.Transports.GetOrCreate<AmazonSqsTransport>();
        var configuration = new AmazonSqsTransportConfiguration(transport, options);

        if (deadLetterQueueNames.Length == 0)
        {
            configuration.EnableDeadLetterQueueRecovery();
        }
        else
        {
            configuration.EnableDeadLetterQueueRecovery(deadLetterQueueNames);
        }

        return transport;
    }

    private static AmazonSqsQueue listeningQueue(AmazonSqsTransport transport, string name)
    {
        var queue = transport.Queues[name];
        queue.IsListener = true;
        return queue;
    }

    [Fact]
    public void listening_queue_is_native_with_recovery_when_recovery_covers_everything()
    {
        var transport = transportWithRecovery();

        listeningQueue(transport, "orders").DeadLetterStorage.ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
    }

    [Fact]
    public void queue_that_is_not_listening_stays_native_because_recovery_only_drains_listeners()
    {
        var transport = transportWithRecovery();

        transport.Queues["outgoing"].DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Native);
    }

    [Fact]
    public void queue_is_native_with_recovery_only_when_its_dead_letter_queue_is_listed()
    {
        var transport = transportWithRecovery("orders-dlq");

        var orders = listeningQueue(transport, "orders");
        orders.DeadLetterQueueName = "orders-dlq";
        orders.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.NativeWithRecovery);

        // Still on the default dead letter queue, which is not in the list
        listeningQueue(transport, "payments").DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Native);
    }

    [Fact]
    public void listing_the_default_dead_letter_queue_covers_queues_that_use_it()
    {
        var transport = transportWithRecovery(AmazonSqsTransport.DeadLetterQueueName);

        listeningQueue(transport, "orders").DeadLetterStorage.ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
    }

    [Fact]
    public void listed_names_are_compared_after_sanitizing()
    {
        var transport = transportWithRecovery("acme.orders.dlq");

        var orders = listeningQueue(transport, "orders");
        orders.DeadLetterQueueName = "acme-orders-dlq";
        orders.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
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
    public void durable_when_all_native_dead_lettering_disabled_with_recovery_on()
    {
        var transport = transportWithRecovery();
        transport.DisableDeadLetterQueues = true;

        listeningQueue(transport, "orders").DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Durable);
    }
}
