using TestMessages;
using Wolverine.Attributes;
using Xunit;

namespace CoreTests.Acceptance;

public class using_error_handling_attributes : IntegrationContext
{
    public using_error_handling_attributes(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public void use_maximum_attempts()
    {
        chainFor<Message1>().Failures.MaximumAttempts.ShouldBe(3);
    }
}

public class ErrorCausingConsumer
{
    [MaximumAttempts(3)]
    public void Handle(Message1 message)
    {
    }

    [RetryNow(typeof(DivideByZeroException), 10, 20, 50)]
    public void Handle(Message2 message)
    {
    }

    [RequeueOn(typeof(NotImplementedException))]
    public void Handle(Message3 message)
    {
    }

    [MoveToErrorQueueOn(typeof(DataMisalignedException))]
    public void Handle(Message4 message)
    {
    }

    [ScheduleRetry(typeof(DivideByZeroException), 5)]
    public void Handle(Message5 message)
    {
    }
}