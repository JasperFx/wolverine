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
}
