using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
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

        await flushed.Task.WaitAsync(2.Seconds());
        sw.Stop();

        sw.Elapsed.ShouldBeGreaterThanOrEqualTo(40.Milliseconds());
        sw.Elapsed.ShouldBeLessThan(500.Milliseconds());
    }

    [Fact]
    public void default_batch_timeout_is_250ms()
    {
        new TcpEndpoint(2257).MessageBatchTimeout.ShouldBe(250.Milliseconds());
    }
}