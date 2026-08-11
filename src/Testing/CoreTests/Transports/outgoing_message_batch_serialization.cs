using Shouldly;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
/// <see cref="OutgoingMessageBatch" /> used to serialize the whole batch into one contiguous buffer in its
/// constructor. Only the TCP protocol ever reads that buffer; SQS, Rabbit, Service Bus and Kafka all read
/// <see cref="Envelope.Data" /> per message and chunk to their own limits, so for them it was a large
/// allocation that was built and thrown away.
///
/// That is not only waste. Measured on a production projection rebuild sending metrics over SQS: at the
/// default MessageBatchSize of 100 with large payloads, that single allocation threw
/// <c>OutOfMemoryException</c> from <c>MemoryStream.set_Capacity</c> and took the process down at ~1.5 GB
/// working set against an 8 GB limit — nowhere near heap exhaustion, because the failure is one contiguous
/// buffer rather than the heap as a whole.
/// </summary>
public class outgoing_message_batch_serialization
{
    private static Envelope envelopeWith(int payloadBytes) => new()
    {
        Id = Guid.NewGuid(),
        MessageType = "metrics",
        Data = new byte[payloadBytes]
    };

    [Fact]
    public void does_not_serialize_until_the_buffer_is_actually_read()
    {
        var batch = new OutgoingMessageBatch(new Uri("sqs://critterwatch"),
            [envelopeWith(1024), envelopeWith(1024)]);

        // Nothing has asked for the contiguous buffer, so nothing should have been built. Reaching
        // through the property would defeat the point of the test, so check the backing field.
        var field = typeof(OutgoingMessageBatch)
            .GetField("_data", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.ShouldNotBeNull();
        field!.GetValue(batch).ShouldBeNull();
    }

    [Fact]
    public void still_serializes_correctly_when_the_buffer_is_read()
    {
        var batch = new OutgoingMessageBatch(new Uri("tcp://localhost:2222"),
            [envelopeWith(64), envelopeWith(64)]);

        batch.Data.ShouldNotBeNull();
        batch.Data.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void reads_the_same_buffer_twice_without_rebuilding_it()
    {
        var batch = new OutgoingMessageBatch(new Uri("tcp://localhost:2222"), [envelopeWith(64)]);

        batch.Data.ShouldBeSameAs(batch.Data);
    }

    [Fact]
    public void an_explicitly_assigned_buffer_wins()
    {
        // WireProtocol assigns Data on the receiving side; that path must keep working.
        var batch = new OutgoingMessageBatch(new Uri("tcp://localhost:2222"), [envelopeWith(64)])
        {
            Data = [1, 2, 3]
        };

        batch.Data.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void the_destination_is_stamped_on_every_envelope_regardless()
    {
        // This happened in the constructor alongside the serialization; it must not have moved with it.
        var destination = new Uri("sqs://critterwatch");
        var batch = new OutgoingMessageBatch(destination, [envelopeWith(64), envelopeWith(64)]);

        batch.Messages.ShouldAllBe(e => e.Destination == destination);
    }
}
