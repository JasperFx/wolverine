using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
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
        theSender = new BatchedSender(TransportConstants.RepliesUri, theProtocol, theCancellation.Token,
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
        await theSender.SendBatchAsync(theBatch);

#pragma warning disable 4014
        theProtocol.Received().SendBatchAsync(theSenderCallback, theBatch);
#pragma warning restore 4014
    }

    [Fact]
    public async Task do_not_call_send_batch_if_cancelled()
    {
        theCancellation.Cancel();

        await theSender.SendBatchAsync(theBatch);

#pragma warning disable 4014
        theProtocol.DidNotReceive().SendBatchAsync(theSenderCallback, theBatch);
#pragma warning restore 4014
    }

    [Fact]
    public async Task do_not_call_send_batch_if_latched()
    {
        await theSender.LatchAndDrainAsync();

        await theSender.SendBatchAsync(theBatch);

#pragma warning disable 4014
        theProtocol.DidNotReceive().SendBatchAsync(theSenderCallback, theBatch);

        theSenderCallback.Received().MarkSenderIsLatchedAsync(theBatch);
#pragma warning restore 4014
    }
}
