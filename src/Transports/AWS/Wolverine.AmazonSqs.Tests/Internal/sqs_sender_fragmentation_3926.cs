using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Tests.Internal;

/// <summary>
/// GH-3926. What the sender does with a message too big for SQS: discard it when the endpoint has not
/// opted in (SQS answers an oversized message with a permanent SenderFault, so retrying it produces a
/// flood of identical errors), or split it into fragments when it has.
/// </summary>
public class sqs_sender_fragmentation_3926
{
    private readonly ISenderCallback _callback = Substitute.For<ISenderCallback>();
    private readonly AmazonSqsQueue _queue;
    private readonly IAmazonSQS _sqs = Substitute.For<IAmazonSQS>();
    private readonly SqsSenderProtocol _protocol;

    public sqs_sender_fragmentation_3926()
    {
        var transport = new AmazonSqsTransport { Client = _sqs };
        _queue = new AmazonSqsQueue("big", transport) { Mapper = new DefaultSqsEnvelopeMapper() };

        _sqs.GetQueueUrlAsync("big", Arg.Any<CancellationToken>())
            .Returns(new GetQueueUrlResponse { QueueUrl = "https://sqs.local/big" });

        _sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageBatchResponse());

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);

        _protocol = new SqsSenderProtocol(runtime, _queue, _sqs);
    }

    /// <summary>
    /// The default mapper base64 encodes the serialized envelope, so the body is roughly 4/3 of the
    /// payload. 400KB of data comfortably clears the fragment size and needs three fragments.
    /// </summary>
    private static Envelope oversized(int bytes = 400 * 1024)
    {
        return new Envelope
        {
            Id = Guid.NewGuid(),
            Data = new byte[bytes],
            MessageType = "big.message"
        };
    }

    private static Envelope ordinary()
    {
        return new Envelope { Id = Guid.NewGuid(), Data = [1, 2, 3], MessageType = "small.message" };
    }

    private OutgoingMessageBatch batchFor(params Envelope[] envelopes)
    {
        return new OutgoingMessageBatch(_queue.Uri, envelopes);
    }

    private List<SendMessageBatchRequestEntry> sentEntries()
    {
        return _sqs.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(IAmazonSQS.SendMessageBatchAsync))
            .Select(x => (SendMessageBatchRequest)x.GetArguments()[0]!)
            .SelectMany(x => x.Entries)
            .ToList();
    }

    [Fact]
    public async Task an_oversized_message_is_discarded_rather_than_retried_forever_when_not_opted_in()
    {
        var envelope = oversized();

        await _protocol.SendBatchAsync(_callback, batchFor(envelope));

        // Not MarkProcessingFailureAsync -- that would re-queue it, and the next attempt gets the same
        // SenderFault from SQS, forever.
        await _callback.Received(1).MarkSerializationFailureAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Count == 1 && b.Messages.Contains(envelope)));

        await _callback.DidNotReceive().MarkProcessingFailureAsync(Arg.Any<OutgoingMessageBatch>());
        await _callback.DidNotReceive()
            .MarkProcessingFailureAsync(Arg.Any<OutgoingMessageBatch>(), Arg.Any<Exception>());

        await _sqs.DidNotReceive().SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task the_rest_of_the_batch_still_goes_out_around_a_discarded_message()
    {
        var good1 = ordinary();
        var bad = oversized();
        var good2 = ordinary();

        await _protocol.SendBatchAsync(_callback, batchFor(good1, bad, good2));

        await _callback.Received(1).MarkSerializationFailureAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Count == 1 && b.Messages.Contains(bad)));

        await _callback.Received(1).MarkSuccessfulAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Count == 2 && b.Messages.Contains(good1) && b.Messages.Contains(good2)));
    }

    [Fact]
    public async Task an_oversized_message_is_split_into_fragments_when_opted_in()
    {
        _queue.FragmentOversizedMessages = true;
        var envelope = oversized();

        await _protocol.SendBatchAsync(_callback, batchFor(envelope));

        var entries = sentEntries();
        entries.Count.ShouldBeGreaterThan(1);

        // Every entry carries the framing, all of it pointing at the one envelope
        foreach (var entry in entries)
        {
            entry.MessageAttributes.ContainsKey(SqsMessageFragments.FragmentIdAttribute).ShouldBeTrue();
            entry.MessageAttributes[SqsMessageFragments.FragmentIdAttribute].StringValue
                .ShouldBe(envelope.Id.ToString());
            entry.MessageAttributes[SqsMessageFragments.FragmentCountAttribute].StringValue
                .ShouldBe(entries.Count.ToString());
        }

        entries.Select(x => x.MessageAttributes[SqsMessageFragments.FragmentIndexAttribute].StringValue)
            .ShouldBe(Enumerable.Range(0, entries.Count).Select(i => i.ToString()));

        // Entry ids have to be unique per fragment or SQS rejects the request outright
        entries.Select(x => x.Id).Distinct().Count().ShouldBe(entries.Count);

        await _callback.Received(1).MarkSuccessfulAsync(Arg.Any<OutgoingMessageBatch>());
        await _callback.DidNotReceive().MarkSerializationFailureAsync(Arg.Any<OutgoingMessageBatch>());
    }

    [Fact]
    public async Task the_fragments_reassemble_into_the_original_body()
    {
        _queue.FragmentOversizedMessages = true;
        var envelope = oversized(300 * 1024);
        envelope.Data = Enumerable.Range(0, 300 * 1024).Select(i => (byte)i).ToArray();

        await _protocol.SendBatchAsync(_callback, batchFor(envelope));

        // Concatenating the fragments in index order and reading them back through the mapper is exactly
        // what the listener does, so this is the round trip that matters rather than string equality
        // against a body built separately.
        var reassembled = string.Concat(sentEntries().Select(x => x.MessageBody));

        var received = new Envelope();
        _queue.Mapper!.ReadEnvelopeData(received, reassembled, new Dictionary<string, MessageAttributeValue>());

        received.Id.ShouldBe(envelope.Id);
        received.MessageType.ShouldBe(envelope.MessageType);
        received.Data.ShouldBe(envelope.Data);
    }

    [Fact]
    public async Task each_fragment_gets_its_own_request_rather_than_blowing_the_batch_limit()
    {
        _queue.FragmentOversizedMessages = true;

        await _protocol.SendBatchAsync(_callback, batchFor(oversized()));

        var requests = _sqs.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(IAmazonSQS.SendMessageBatchAsync))
            .Select(x => (SendMessageBatchRequest)x.GetArguments()[0]!)
            .ToList();

        requests.ShouldAllBe(r => r.Entries.Sum(e => e.MessageBody.Length)
                                  <= SqsSenderProtocol.MaximumBatchPayloadBytes);
    }

    [Fact]
    public async Task a_message_too_large_even_to_fragment_is_discarded()
    {
        _queue.FragmentOversizedMessages = true;

        // Well past MaximumFragments once base64 inflation is applied
        var envelope = oversized(4 * 1024 * 1024);

        await _protocol.SendBatchAsync(_callback, batchFor(envelope));

        await _callback.Received(1).MarkSerializationFailureAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Contains(envelope)));

        await _sqs.DidNotReceive().SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task one_rejected_fragment_fails_the_whole_envelope()
    {
        _queue.FragmentOversizedMessages = true;
        var envelope = oversized();

        // Reject the second fragment. Half a message in SQS can never be reassembled, so the envelope
        // has to be retried in full rather than reported as delivered.
        _sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new SendMessageBatchResponse(),
                call => new SendMessageBatchResponse
                {
                    Failed =
                    [
                        new BatchResultErrorEntry
                        {
                            Id = ((SendMessageBatchRequest)call[0]!).Entries[0].Id,
                            Code = "ThrottlingException",
                            Message = "Rate exceeded",
                            SenderFault = false
                        }
                    ]
                });

        await _protocol.SendBatchAsync(_callback, batchFor(envelope));

        await _callback.Received(1).MarkProcessingFailureAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Count == 1 && b.Messages.Contains(envelope)));

        await _callback.DidNotReceive().MarkSuccessfulAsync(Arg.Any<OutgoingMessageBatch>());
    }

    [Fact]
    public async Task fragments_of_one_message_share_a_group_id_on_a_fifo_queue()
    {
        var transport = new AmazonSqsTransport { Client = _sqs };
        var fifo = new AmazonSqsQueue("big.fifo", transport)
        {
            Mapper = new DefaultSqsEnvelopeMapper(),
            FragmentOversizedMessages = true
        };

        _sqs.GetQueueUrlAsync("big.fifo", Arg.Any<CancellationToken>())
            .Returns(new GetQueueUrlResponse { QueueUrl = "https://sqs.local/big.fifo" });

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);

        var protocol = new SqsSenderProtocol(runtime, fifo, _sqs);

        var envelope = oversized();
        envelope.DeduplicationId = "dedupe-me";

        await protocol.SendBatchAsync(_callback, new OutgoingMessageBatch(fifo.Uri, [envelope]));

        var entries = sentEntries();
        entries.Count.ShouldBeGreaterThan(1);

        // One group keeps the whole set on one consumer, in order...
        entries.Select(x => x.MessageGroupId).Distinct().Count().ShouldBe(1);

        // ...but a shared deduplication id would have FIFO keep exactly one of them
        entries.Select(x => x.MessageDeduplicationId).Distinct().Count().ShouldBe(entries.Count);
    }
}
