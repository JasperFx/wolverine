using Shouldly;
using Wolverine.AmazonSqs.Internal;

namespace Wolverine.AmazonSqs.Tests.Internal;

/// <summary>
/// GH-3493 (SO3). SQS caps a whole SendMessageBatch request at 256KB, not just each message, so
/// chunking on the ten-entry limit alone let ten individually legal messages bounce the request.
/// </summary>
public class sqs_batch_chunking_3493
{
    private static Envelope envelopeOfSize(int bytes)
    {
        return new Envelope
        {
            Id = Guid.NewGuid(),
            Data = new byte[bytes]
        };
    }

    [Fact]
    public void chunks_small_messages_at_the_ten_entry_limit()
    {
        var envelopes = Enumerable.Range(0, 25).Select(_ => envelopeOfSize(100)).ToArray();

        var chunks = SqsSenderProtocol.ChunkMessages(envelopes);

        chunks.Length.ShouldBe(3);
        chunks[0].Length.ShouldBe(10);
        chunks[1].Length.ShouldBe(10);
        chunks[2].Length.ShouldBe(5);
    }

    [Fact]
    public void chunks_large_messages_on_the_request_payload_limit()
    {
        // Ten of these are each legal on their own but together blow past 256KB.
        var envelopes = Enumerable.Range(0, 10).Select(_ => envelopeOfSize(60 * 1024)).ToArray();

        var chunks = SqsSenderProtocol.ChunkMessages(envelopes);

        chunks.Length.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(chunk =>
            chunk.Sum(x => SqsSenderProtocol.EstimateEntrySize(x)) <= SqsSenderProtocol.MaximumBatchPayloadBytes
            || chunk.Length == 1);
        chunks.SelectMany(x => x).Count().ShouldBe(10);
    }

    [Fact]
    public void an_oversized_single_message_still_gets_its_own_chunk()
    {
        var envelopes = new[] { envelopeOfSize(400 * 1024), envelopeOfSize(100) };

        var chunks = SqsSenderProtocol.ChunkMessages(envelopes);

        chunks.Length.ShouldBe(2);
        chunks[0].Length.ShouldBe(1);
        chunks[1].Length.ShouldBe(1);
    }

    [Fact]
    public void no_messages_are_dropped_or_duplicated()
    {
        var envelopes = Enumerable.Range(0, 37).Select(i => envelopeOfSize(i * 2000)).ToArray();

        var chunks = SqsSenderProtocol.ChunkMessages(envelopes);

        chunks.SelectMany(x => x).ShouldBe(envelopes);
    }

    [Fact]
    public void empty_batch_yields_no_chunks()
    {
        SqsSenderProtocol.ChunkMessages([]).ShouldBeEmpty();
    }

    [Fact]
    public void estimate_accounts_for_base64_inflation()
    {
        // 3 raw bytes become 4 base64 characters, so the estimate must exceed the raw length.
        SqsSenderProtocol.EstimateEntrySize(envelopeOfSize(30_000))
            .ShouldBeGreaterThan(30_000 + SqsSenderProtocol.EntryOverheadBytes);
    }
}

/// <summary>
/// GH-3493 (SO1). Guard rails on the delete batching knob.
/// </summary>
public class sqs_delete_batch_configuration_3493
{
    [Fact]
    public void defaults_to_the_sqs_maximum()
    {
        var queue = new AmazonSqsQueue("foo", new AmazonSqsTransport());
        queue.DeleteMessageBatchSize.ShouldBe(AmazonSqsQueue.MaximumDeleteBatchSize);
        queue.DeleteMessageBatchTimeout.ShouldBe(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void one_is_allowed_as_the_opt_out()
    {
        var queue = new AmazonSqsQueue("foo", new AmazonSqsTransport()) { DeleteMessageBatchSize = 1 };
        queue.DeleteMessageBatchSize.ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void rejects_sizes_outside_the_sqs_limits(int size)
    {
        var queue = new AmazonSqsQueue("foo", new AmazonSqsTransport());
        Should.Throw<ArgumentOutOfRangeException>(() => queue.DeleteMessageBatchSize = size);
    }
}
