using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Runtime;

public class ScheduledMessageCancellationTests
{
    [Fact]
    public async Task BeforeBrokerAcceptanceReportsNotScheduledYetAndCanBeRetried()
    {
        var registration = new ScheduledMessageCancellationRegistration(Guid.NewGuid());
        var beforeSend = await registration.CancelAsync(CancellationToken.None);
        beforeSend.Status.ShouldBe(ScheduledMessageCancellationStatus.NotScheduledYet);

        var calls = 0;
        registration.TryBind(_ =>
        {
            calls++;
            return ValueTask.FromResult(ScheduledMessageCancellationStatus.CancellationAccepted);
        }).ShouldBeTrue();

        var afterSend = await registration.CancelAsync(CancellationToken.None);
        afterSend.Status.ShouldBe(ScheduledMessageCancellationStatus.CancellationAccepted);
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task AcceptedCancellationIsCachedForRepeatedCalls()
    {
        var registration = new ScheduledMessageCancellationRegistration(Guid.NewGuid());
        var calls = 0;
        registration.TryBind(_ =>
        {
            calls++;
            return ValueTask.FromResult(ScheduledMessageCancellationStatus.CancellationAccepted);
        }).ShouldBeTrue();

        var first = await registration.CancelAsync(CancellationToken.None);
        var second = await registration.CancelAsync(CancellationToken.None);

        first.Status.ShouldBe(ScheduledMessageCancellationStatus.CancellationAccepted);
        second.Status.ShouldBe(ScheduledMessageCancellationStatus.CancellationAccepted);
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task RegistryRetainsBoundCancellationWhileHandleIsAlive()
    {
        var registration = new ScheduledMessageCancellationRegistration(Guid.NewGuid());
        var registry = new ScheduledMessageCancellationRegistry();
        registry.Register(registration);
        registration.TryBind(_ => ValueTask.FromResult(ScheduledMessageCancellationStatus.CancellationAccepted));

        registry.Find(registration.EnvelopeId).ShouldBeSameAs(registration);
        (await registry.Find(registration.EnvelopeId)!.CancelAsync(CancellationToken.None)).Status
            .ShouldBe(ScheduledMessageCancellationStatus.CancellationAccepted);
    }

    [Fact]
    public async Task InitialOutcomesDoNotCallABroker()
    {
        var unsupported = new ScheduledMessageCancellationRegistration(Guid.NewGuid(),
            ScheduledMessageCancellationStatus.NotSupported);
        var alreadyDue = new ScheduledMessageCancellationRegistration(Guid.NewGuid(),
            ScheduledMessageCancellationStatus.NotScheduled);

        (await unsupported.CancelAsync(CancellationToken.None)).Status
            .ShouldBe(ScheduledMessageCancellationStatus.NotSupported);
        (await alreadyDue.CancelAsync(CancellationToken.None)).Status
            .ShouldBe(ScheduledMessageCancellationStatus.NotScheduled);
    }

    [Fact]
    public async Task DueTimePassingBeforeSendMarksTheHandleNotScheduled()
    {
        var registration = new ScheduledMessageCancellationRegistration(Guid.NewGuid());
        registration.MarkNotScheduled();
        registration.CanBeBound.ShouldBeFalse();

        (await registration.CancelAsync(CancellationToken.None)).Status
            .ShouldBe(ScheduledMessageCancellationStatus.NotScheduled);
    }

    [Fact]
    public async Task FailedCancellationCanBeRetried()
    {
        var registration = new ScheduledMessageCancellationRegistration(Guid.NewGuid());
        var calls = 0;
        registration.TryBind(_ =>
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("temporary broker error");
            return ValueTask.FromResult(ScheduledMessageCancellationStatus.NoLongerScheduled);
        }).ShouldBeTrue();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await registration.CancelAsync(CancellationToken.None));

        var retry = await registration.CancelAsync(CancellationToken.None);
        retry.Status.ShouldBe(ScheduledMessageCancellationStatus.NoLongerScheduled);
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task FanOutReportsOneOutcomePerEnvelope()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = new ScheduledMessageCancellationRegistration(firstId);
        first.TryBind(_ => ValueTask.FromResult(ScheduledMessageCancellationStatus.CancellationAccepted));
        var second = new ScheduledMessageCancellationRegistration(secondId,
            ScheduledMessageCancellationStatus.NotSupported);
        var handle = new ScheduledMessageCancellationHandle([first, second]);

        var results = await handle.CancelAsync(TestContext.Current.CancellationToken);

        handle.EnvelopeIds.ShouldBe([firstId, secondId]);
        results.Select(x => x.EnvelopeId).ShouldBe([firstId, secondId]);
        results.Select(x => x.Status).ShouldBe([
            ScheduledMessageCancellationStatus.CancellationAccepted,
            ScheduledMessageCancellationStatus.NotSupported
        ]);
    }
}
