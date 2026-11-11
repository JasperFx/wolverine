using Wolverine.ComplianceTests.Compliance;
using Wolverine.Attributes;
using Xunit;

namespace CoreTests.Acceptance;

public class using_error_handling_attributes : IntegrationContext
{
    public using_error_handling_attributes(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task use_maximum_attempts()
    {
        await with(opts => opts.Discovery.IncludeType<ErrorCausingConsumer>());

        chainFor<Message1, ErrorCausingConsumer>().Failures.MaximumAttempts.ShouldBe(3);
    }
}

// GH-5136. Hidden from conventional discovery: it handles the shared Message1-5, and every handler type of a
// message type now gets its own chain, so leaking it into every other host in this assembly would give those
// messages a second handler there
[WolverineIgnore]
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