using System;
using TestMessages;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

public class WaitForMessageTester
{
    [Theory]
    [InlineData(MessageEventType.Received, typeof(Message1), false)]
    [InlineData(MessageEventType.Sent, typeof(Message1), false)]
    [InlineData(MessageEventType.ExecutionFinished, typeof(Message1), false)]
    [InlineData(MessageEventType.ExecutionStarted, typeof(Message1), false)]
    [InlineData(MessageEventType.MessageFailed, typeof(Message1), true)]
    [InlineData(MessageEventType.MessageSucceeded, typeof(Message1), true)]
    [InlineData(MessageEventType.NoHandlers, typeof(Message1), false)]
    [InlineData(MessageEventType.NoRoutes, typeof(Message1), false)]
    public void is_completed_with_no_unique_id(MessageEventType eventType, Type messageType, bool isCompleted)
    {
        var waiter = new WaitForMessage<Message1>();

        var message = Activator.CreateInstance(messageType);

        waiter.Record(new EnvelopeRecord(eventType, new Envelope(message), 100, null));

        waiter.IsCompleted().ShouldBe(isCompleted);
    }

    public static Guid[] guids = new[]
    {
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
    };

    [Theory]
    [InlineData(MessageEventType.Received, typeof(Message1), 1, false)]
    [InlineData(MessageEventType.Sent, typeof(Message1), 1, false)]
    [InlineData(MessageEventType.ExecutionFinished, typeof(Message1), 1, false)]
    [InlineData(MessageEventType.ExecutionStarted, typeof(Message1), 1, false)]
    [InlineData(MessageEventType.MessageFailed, typeof(Message1), 1, false)]
    [InlineData(MessageEventType.MessageSucceeded, typeof(Message1), 1, false)]
    [InlineData(MessageEventType.MessageFailed, typeof(Message1), 5, true)]
    [InlineData(MessageEventType.MessageSucceeded, typeof(Message1), 5, true)]
    [InlineData(MessageEventType.NoHandlers, typeof(Message1), 1, false)]
    [InlineData(MessageEventType.NoRoutes, typeof(Message1), 1, false)]
    public void is_completed_with_unique_id(MessageEventType eventType, Type messageType, int nodeId, bool isCompleted)
    {
        var waiter = new WaitForMessage<Message1>
        {
            UniqueNodeId = guids[5]
        };

        var message = Activator.CreateInstance(messageType);

        waiter.Record(new EnvelopeRecord(eventType, new Envelope(message), 100, null)
        {
            UniqueNodeId = guids[nodeId]
        });

        waiter.IsCompleted().ShouldBe(isCompleted);
    }
}