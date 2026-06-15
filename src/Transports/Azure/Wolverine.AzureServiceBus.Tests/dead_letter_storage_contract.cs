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
}
