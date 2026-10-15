using Wolverine;
using Xunit;

namespace CoreTests.Runtime;

public class TestMessageContextScheduledCancellationTests
{
    [Fact]
    public async Task SchedulesAtRequestedTimeAndReportsUnsupportedCancellation()
    {
        var context = new TestMessageContext();
        IMessageBus bus = context;
        var message = new TestScheduledMessage();
        var time = DateTimeOffset.UtcNow.AddMinutes(5);

        var handle = await bus.ScheduleWithCancellationAsync(message, time);

        var scheduled = context.ScheduledMessages().Single();
        scheduled.ScheduledTime.ShouldBe(time);
        var result = (await handle.CancelAsync(TestContext.Current.CancellationToken)).Single();
        result.EnvelopeId.ShouldBe(scheduled.Id);
        result.Status.ShouldBe(ScheduledMessageCancellationStatus.NotSupported);
    }

    [Fact]
    public async Task SchedulesByDelayAndReportsUnsupportedCancellation()
    {
        var context = new TestMessageContext();
        IMessageBus bus = context;
        var message = new TestScheduledMessage();

        var handle = await bus.ScheduleWithCancellationAsync(message, TimeSpan.FromMinutes(5));

        context.ScheduledMessages().Single().ScheduledTime.ShouldNotBeNull();
        (await handle.CancelAsync(TestContext.Current.CancellationToken)).Single().Status
            .ShouldBe(ScheduledMessageCancellationStatus.NotSupported);
    }

    public record TestScheduledMessage;
}
