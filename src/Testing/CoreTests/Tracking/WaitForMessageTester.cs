using System;
using Shouldly;
using TestMessages;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

public class WaitForMessageTester
{
    [Theory]
    [InlineData(EventType.Received, typeof(Message1), false)]
    [InlineData(EventType.Sent, typeof(Message1), false)]
    [InlineData(EventType.ExecutionFinished, typeof(Message1), false)]
    [InlineData(EventType.ExecutionStarted, typeof(Message1), false)]
    [InlineData(EventType.MessageFailed, typeof(Message1), true)]
    [InlineData(EventType.MessageSucceeded, typeof(Message1), true)]
    [InlineData(EventType.NoHandlers, typeof(Message1), false)]
    [InlineData(EventType.NoRoutes, typeof(Message1), false)]
    public void is_completed_with_no_unique_id(EventType eventType, Type messageType, bool isCompleted)
    {
        var waiter = new WaitForMessage<Message1>();

        var message = Activator.CreateInstance(messageType);

        waiter.Record(new EnvelopeRecord(eventType, new Envelope(message), 100, null));

        waiter.IsCompleted().ShouldBe(isCompleted);
    }

    [Theory]
    [InlineData(EventType.Received, typeof(Message1), 1, false)]
    [InlineData(EventType.Sent, typeof(Message1), 1, false)]
    [InlineData(EventType.ExecutionFinished, typeof(Message1), 1, false)]
    [InlineData(EventType.ExecutionStarted, typeof(Message1), 1, false)]
    [InlineData(EventType.MessageFailed, typeof(Message1), 1, false)]
    [InlineData(EventType.MessageSucceeded, typeof(Message1), 1, false)]
    [InlineData(EventType.MessageFailed, typeof(Message1), 5, true)]
    [InlineData(EventType.MessageSucceeded, typeof(Message1), 5, true)]
    [InlineData(EventType.NoHandlers, typeof(Message1), 1, false)]
    [InlineData(EventType.NoRoutes, typeof(Message1), 1, false)]
    public void is_completed_with_unique_id(EventType eventType, Type messageType, int nodeId, bool isCompleted)
    {
        var waiter = new WaitForMessage<Message1>
        {
            UniqueNodeId = 5
        };

        var message = Activator.CreateInstance(messageType);

        waiter.Record(new EnvelopeRecord(eventType, new Envelope(message), 100, null)
        {
            UniqueNodeId = nodeId
        });

        waiter.IsCompleted().ShouldBe(isCompleted);
    }
}
