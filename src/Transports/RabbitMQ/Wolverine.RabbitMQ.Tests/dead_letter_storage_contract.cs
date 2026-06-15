using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// Coverage for the transport-agnostic dead-letter-destination contract (GH-3104) as reported by
/// RabbitMQ queues: native dead lettering reports <see cref="DeadLetterStorageMode.Native"/>,
/// <c>EnableDeadLetterQueueRecovery()</c> promotes it to
/// <see cref="DeadLetterStorageMode.NativeWithRecovery"/>, and <c>WolverineStorage</c> mode reports
/// <see cref="DeadLetterStorageMode.Durable"/>.
/// </summary>
public class dead_letter_storage_contract
{
    [Fact]
    public void native_by_default()
    {
        var transport = new RabbitMqTransport();
        transport.Queues["orders"].DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Native);
    }

    [Fact]
    public void native_with_recovery_when_recovery_enabled()
    {
        var transport = new RabbitMqTransport { EnableDeadLetterQueueRecovery = true };
        transport.Queues["orders"].DeadLetterStorage.ShouldBe(DeadLetterStorageMode.NativeWithRecovery);
    }

    [Fact]
    public void durable_when_using_wolverine_storage_mode()
    {
        var transport = new RabbitMqTransport();
        var queue = transport.Queues["orders"];
        queue.DeadLetterQueue!.Mode = DeadLetterQueueMode.WolverineStorage;

        queue.DeadLetterStorage.ShouldBe(DeadLetterStorageMode.Durable);
    }
}
