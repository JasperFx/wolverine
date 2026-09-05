using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Transports.Sending;

public class BatchedSenderTests
{
    private readonly OutgoingMessageBatch theBatch;
    private readonly CancellationTokenSource theCancellation = new();

    private readonly ISenderProtocol theProtocol = Substitute.For<ISenderProtocol>();
    private readonly BatchedSender theSender;
    private readonly ISenderCallback theSenderCallback = Substitute.For<ISenderCallback>();

    public BatchedSenderTests()
    {
        theSender = new BatchedSender(new TcpEndpoint(2255), theProtocol, theCancellation.Token,
            NullLogger.Instance);

        theSender.RegisterCallback(theSenderCallback);

        theBatch = new OutgoingMessageBatch(theSender.Destination, new[]
        {
            Envelope.ForPing(TransportConstants.LocalUri),
            Envelope.ForPing(TransportConstants.LocalUri),
            Envelope.ForPing(TransportConstants.LocalUri),
            Envelope.ForPing(TransportConstants.LocalUri),
            Envelope.ForPing(TransportConstants.LocalUri),
            Envelope.ForPing(TransportConstants.LocalUri)
        });

        theBatch.Messages.Each(x => x.Destination = theBatch.Destination);
    }

    [Fact]
    public async Task call_send_batch_if_not_latched_and_not_cancelled()
    {
        await theSender.SendBatchAsync(theBatch, CancellationToken.None);

#pragma warning disable 4014
        theProtocol.Received().SendBatchAsync(theSenderCallback, theBatch);
#pragma warning restore 4014
    }

    [Fact]
    public async Task do_not_actually_send_outgoing_batched_when_the_system_is_trying_to_shut_down()
    {
        // This is a cancellation token for the subsystem being tested
        await theCancellation.CancelAsync();

        // This is the "action"
        await theSender.SendBatchAsync(theBatch, CancellationToken.None);

        // Do not send on the batch of messages if the
        // underlying cancellation token has been marked
        // as cancelled
        await theProtocol.DidNotReceive()
            .SendBatchAsync(theSenderCallback, theBatch);
    }

    [Fact]
    public async Task do_not_call_send_batch_if_latched()
    {
        await theSender.LatchAndDrainAsync();

        await theSender.SendBatchAsync(theBatch, CancellationToken.None);

#pragma warning disable 4014
        theProtocol.DidNotReceive().SendBatchAsync(theSenderCallback, theBatch);

        theSenderCallback.Received().MarkSenderIsLatchedAsync(theBatch);
#pragma warning restore 4014
    }

    [Fact]
    public async Task flushes_partial_batch_after_configured_timeout()
    {
        // A sub-batch-size payload will only flush because of the timer, so the elapsed
        // time is a direct read of MessageBatchTimeout.
        var endpoint = new TcpEndpoint(2256)
        {
            MessageBatchSize = 100,
            MessageBatchTimeout = 50.Milliseconds()
        };

        var flushed = new TaskCompletionSource();
        var protocol = Substitute.For<ISenderProtocol>();
        protocol.SendBatchAsync(Arg.Any<ISenderCallback>(), Arg.Any<OutgoingMessageBatch>())
            .Returns(_ =>
            {
                flushed.TrySetResult();
                return Task.CompletedTask;
            });

        var callback = Substitute.For<ISenderCallback>();
        using var sender = new BatchedSender(endpoint, protocol, CancellationToken.None, NullLogger.Instance);
        sender.RegisterCallback(callback);

        var sw = Stopwatch.StartNew();
        await sender.SendAsync(Envelope.ForPing(TransportConstants.LocalUri));

        await flushed.Task.WaitAsync(2.Seconds(), TestContext.Current.CancellationToken);
        sw.Stop();

        sw.Elapsed.ShouldBeGreaterThanOrEqualTo(40.Milliseconds());
        sw.Elapsed.ShouldBeLessThan(500.Milliseconds());
    }

    // The Channels rewrite of this pipeline kept the per-batch decrement of _queued but lost
    // the enqueue-side increment, so QueuedCount drifted negative on every batched sender.
    [Fact]
    public async Task queued_count_tracks_posted_minus_flushed_envelopes()
    {
        var endpoint = new TcpEndpoint(2259)
        {
            MessageBatchSize = 100,
            MessageBatchTimeout = 50.Milliseconds()
        };

        var gate = new TaskCompletionSource();
        var protocol = Substitute.For<ISenderProtocol>();
        protocol.SendBatchAsync(Arg.Any<ISenderCallback>(), Arg.Any<OutgoingMessageBatch>())
            .Returns(_ => gate.Task);

        using var sender = new BatchedSender(endpoint, protocol, CancellationToken.None, NullLogger.Instance);
        sender.RegisterCallback(Substitute.For<ISenderCallback>());

        sender.QueuedCount.ShouldBe(0);

        for (var i = 0; i < 5; i++)
        {
            await sender.SendAsync(Envelope.ForPing(TransportConstants.LocalUri));
        }

        // The protocol is gated, so all five envelopes are accepted but none has flushed
        sender.QueuedCount.ShouldBe(5);

        gate.SetResult();

        var deadline = DateTimeOffset.UtcNow.Add(5.Seconds());
        while (sender.QueuedCount != 0 && DateTimeOffset.UtcNow < deadline)
        {
            sender.QueuedCount.ShouldBeGreaterThanOrEqualTo(0);
            await Task.Delay(25.Milliseconds(), TestContext.Current.CancellationToken);
        }

        sender.QueuedCount.ShouldBe(0);
    }

    [Fact]
    public void default_batch_timeout_is_250ms()
    {
        new TcpEndpoint(2257).MessageBatchTimeout.ShouldBe(250.Milliseconds());
    }

    // GH-3825: the serializing stage upstream of the batching block ran at
    // Environment.ProcessorCount, so envelopes reached the outgoing batch in
    // serialization-completion order instead of enqueue order. That silently broke the FIFO
    // guarantee behind Azure Service Bus sessions, SQS FIFO message groups, and global
    // partitioning. The uneven serializer below is what makes a parallel stage reorder every time.
    [Fact]
    public async Task preserves_enqueue_order_through_to_the_outgoing_batch()
    {
        var endpoint = new TcpEndpoint(2258)
        {
            MessageBatchSize = 100,
            MessageBatchTimeout = 250.Milliseconds()
        };

        var batches = new List<OutgoingMessageBatch>();
        var protocol = Substitute.For<ISenderProtocol>();
        protocol.SendBatchAsync(Arg.Any<ISenderCallback>(), Arg.Any<OutgoingMessageBatch>())
            .Returns(call =>
            {
                lock (batches)
                {
                    batches.Add(call.Arg<OutgoingMessageBatch>());
                }

                return Task.CompletedTask;
            });

        using var sender = new BatchedSender(endpoint, protocol, CancellationToken.None, NullLogger.Instance);
        sender.RegisterCallback(Substitute.For<ISenderCallback>());

        // Descending delays: under any parallelism the last envelope serializes first
        var count = 8;
        var expected = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var name = $"message-{i}";
            expected.Add(name);

            var envelope = new Envelope
            {
                Id = Guid.NewGuid(),
                Destination = sender.Destination,
                Message = name,
                GroupId = name,
                ContentType = "application/staggered",
                Serializer = new StaggeredSerializer((count - i) * 20)
            };

            await sender.SendAsync(envelope);
        }

        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (batches)
            {
                if (batches.SelectMany(x => x.Messages).Count() >= count) break;
            }

            await Task.Delay(50.Milliseconds(), TestContext.Current.CancellationToken);
        }

        List<string> actual;
        lock (batches)
        {
            actual = batches.SelectMany(x => x.Messages).Select(x => x.GroupId!).ToList();
        }

        actual.ShouldBe(expected);
    }
}

internal class StaggeredSerializer : IMessageSerializer
{
    private readonly int _delayInMilliseconds;

    public StaggeredSerializer(int delayInMilliseconds)
    {
        _delayInMilliseconds = delayInMilliseconds;
    }

    public string ContentType => "application/staggered";

    public byte[] Write(Envelope envelope)
    {
        Thread.Sleep(_delayInMilliseconds);
        return [1, 2, 3];
    }

    public object ReadFromData(Type messageType, Envelope envelope) => throw new NotSupportedException();
    public object ReadFromData(byte[] data) => throw new NotSupportedException();
    public byte[] WriteMessage(object message) => throw new NotSupportedException();
}