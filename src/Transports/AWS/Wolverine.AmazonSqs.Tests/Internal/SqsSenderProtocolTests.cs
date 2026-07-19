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

public class SqsSenderProtocolTests
{
    private readonly ISenderCallback _callback = Substitute.For<ISenderCallback>();
    private readonly SqsSenderProtocol _protocol;
    private readonly AmazonSqsQueue _queue;
    private readonly IAmazonSQS _sqs = Substitute.For<IAmazonSQS>();

    public SqsSenderProtocolTests()
    {
        var transport = new AmazonSqsTransport { Client = _sqs };
        _queue = new AmazonSqsQueue("foo", transport)
        {
            Mapper = new DefaultSqsEnvelopeMapper()
        };

        _sqs.GetQueueUrlAsync("foo", Arg.Any<CancellationToken>())
            .Returns(new GetQueueUrlResponse { QueueUrl = "https://sqs.local/foo" });

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);

        _protocol = new SqsSenderProtocol(runtime, _queue, _sqs);
    }

    private static Envelope buildEnvelope()
    {
        return new Envelope
        {
            Data = [1, 2, 3],
            MessageType = "foo.bar"
        };
    }

    private OutgoingMessageBatch batchFor(params Envelope[] envelopes)
    {
        return new OutgoingMessageBatch(_queue.Uri, envelopes);
    }

    [Fact]
    public async Task marks_the_entire_batch_successful_when_no_entries_fail()
    {
        var batch = batchFor(buildEnvelope(), buildEnvelope(), buildEnvelope());

        _sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageBatchResponse());

        await _protocol.SendBatchAsync(_callback, batch);

        await _callback.Received(1).MarkSuccessfulAsync(batch);
        await _callback.DidNotReceive().MarkProcessingFailureAsync(Arg.Any<OutgoingMessageBatch>());
        await _callback.DidNotReceive()
            .MarkProcessingFailureAsync(Arg.Any<OutgoingMessageBatch>(), Arg.Any<Exception>());
    }

    [Fact]
    public async Task partial_failure_reports_only_the_failed_envelopes_as_processing_failures()
    {
        var success1 = buildEnvelope();
        var failed = buildEnvelope();
        var success2 = buildEnvelope();

        var batch = batchFor(success1, failed, success2);

        _sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageBatchResponse
            {
                Failed =
                [
                    new BatchResultErrorEntry
                    {
                        Id = failed.Id.ToString(),
                        Code = "ThrottlingException",
                        Message = "Rate exceeded",
                        SenderFault = false
                    }
                ]
            });

        await _protocol.SendBatchAsync(_callback, batch);

        // The two accepted envelopes are still marked successful
        await _callback.Received(1).MarkSuccessfulAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Count == 2 && b.Messages.Contains(success1) && b.Messages.Contains(success2)));

        // Only the rejected envelope goes down the failure path for retry
        await _callback.Received(1).MarkProcessingFailureAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Count == 1 && b.Messages.Contains(failed)));

        // And the whole original batch was definitely not marked successful
        await _callback.DidNotReceive().MarkSuccessfulAsync(batch);
    }

    [Fact]
    public async Task failed_entry_with_unrecognized_id_is_ignored()
    {
        var batch = batchFor(buildEnvelope(), buildEnvelope());

        _sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageBatchResponse
            {
                Failed =
                [
                    new BatchResultErrorEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        Code = "InternalError",
                        Message = "Unknown entry"
                    }
                ]
            });

        await _protocol.SendBatchAsync(_callback, batch);

        // Nothing in the batch matches the failed entry id, so nothing should be retried
        await _callback.Received(1).MarkSuccessfulAsync(batch);
        await _callback.DidNotReceive().MarkProcessingFailureAsync(Arg.Any<OutgoingMessageBatch>());
        await _callback.DidNotReceive()
            .MarkProcessingFailureAsync(Arg.Any<OutgoingMessageBatch>(), Arg.Any<Exception>());
    }

    [Fact]
    public async Task exception_on_a_later_chunk_only_fails_the_unsent_envelopes()
    {
        // 12 envelopes = 2 chunks against the SQS batch limit of 10
        var envelopes = Enumerable.Range(0, 12).Select(_ => buildEnvelope()).ToArray();
        var batch = batchFor(envelopes);

        var exception = new InvalidOperationException("SQS is down");

        _sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new SendMessageBatchResponse(),
                _ => throw exception);

        await _protocol.SendBatchAsync(_callback, batch);

        // The first chunk of 10 was accepted by SQS and must not be resent
        await _callback.Received(1).MarkSuccessfulAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Count == 10 && envelopes.Take(10).All(e => b.Messages.Contains(e))));

        // Only the two envelopes in the failed chunk are routed to the failure path
        await _callback.Received(1).MarkProcessingFailureAsync(Arg.Is<OutgoingMessageBatch>(b =>
            b.Messages.Count == 2 && b.Messages.Contains(envelopes[10]) && b.Messages.Contains(envelopes[11])),
            exception);

        await _callback.DidNotReceive().MarkSuccessfulAsync(batch);
    }
}
