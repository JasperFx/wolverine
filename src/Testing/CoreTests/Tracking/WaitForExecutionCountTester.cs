using Wolverine.ComplianceTests.Compliance;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

public class WaitForExecutionCountTester
{
    private static EnvelopeRecord recordFor(object message, MessageEventType eventType = MessageEventType.ExecutionFinished)
    {
        return new EnvelopeRecord(eventType, new Envelope(message), 100, null);
    }

    [Fact]
    public void not_completed_until_the_count_is_reached()
    {
        var waiter = new WaitForExecutionCount();
        waiter.ExpectMessage<Message1>(3);

        waiter.IsCompleted().ShouldBeFalse();

        waiter.Record(recordFor(new Message1()));
        waiter.IsCompleted().ShouldBeFalse();

        waiter.Record(recordFor(new Message1()));
        waiter.IsCompleted().ShouldBeFalse();

        waiter.Record(recordFor(new Message1()));
        waiter.IsCompleted().ShouldBeTrue();
    }

    [Fact]
    public void only_execution_finished_records_count()
    {
        var waiter = new WaitForExecutionCount();
        waiter.ExpectMessage<Message1>(1);

        waiter.Record(recordFor(new Message1(), MessageEventType.Sent));
        waiter.Record(recordFor(new Message1(), MessageEventType.Received));
        waiter.Record(recordFor(new Message1(), MessageEventType.ExecutionStarted));

        waiter.IsCompleted().ShouldBeFalse();

        waiter.Record(recordFor(new Message1()));
        waiter.IsCompleted().ShouldBeTrue();
    }

    [Fact]
    public void repeated_executions_of_the_same_envelope_do_not_satisfy_the_count()
    {
        var waiter = new WaitForExecutionCount();
        waiter.ExpectMessage<Message1>(2);

        // Same envelope executing twice, e.g. an inline retry
        var envelope = new Envelope(new Message1());
        waiter.Record(new EnvelopeRecord(MessageEventType.ExecutionFinished, envelope, 100, null));
        waiter.Record(new EnvelopeRecord(MessageEventType.ExecutionFinished, envelope, 200, null));

        waiter.IsCompleted().ShouldBeFalse();

        waiter.Record(recordFor(new Message1()));
        waiter.IsCompleted().ShouldBeTrue();
    }

    [Fact]
    public void all_expectations_must_be_reached()
    {
        var waiter = new WaitForExecutionCount();
        waiter.ExpectMessage<Message1>(2);
        waiter.ExpectMessage<Message2>(1);

        waiter.Record(recordFor(new Message1()));
        waiter.Record(recordFor(new Message1()));
        waiter.IsCompleted().ShouldBeFalse();

        waiter.Record(recordFor(new Message2()));
        waiter.IsCompleted().ShouldBeTrue();
    }

    [Fact]
    public void non_matching_message_types_are_ignored()
    {
        var waiter = new WaitForExecutionCount();
        waiter.ExpectMessage<Message1>(1);

        waiter.Record(recordFor(new Message2()));
        waiter.IsCompleted().ShouldBeFalse();
    }
}
