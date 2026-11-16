using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

public class waiting_for_dead_letter_or_requeue
{
    [Theory]
    [InlineData(MessageEventType.MovedToErrorQueue, true)]
    [InlineData(MessageEventType.MessageFailed, false)]
    [InlineData(MessageEventType.Requeued, false)]
    [InlineData(MessageEventType.MessageSucceeded, false)]
    public void only_a_dead_letter_completes_the_dead_letter_condition(MessageEventType eventType, bool completed)
    {
        var condition = new WaitForMessageEventType<Message1>(MessageEventType.MovedToErrorQueue, "x");

        condition.Record(new EnvelopeRecord(eventType, new Envelope(new Message1()), 100, null));

        condition.IsCompleted().ShouldBe(completed);
    }

    [Fact]
    public void a_dead_letter_of_another_message_type_does_not_complete_it()
    {
        var condition = new WaitForMessageEventType<Message1>(MessageEventType.MovedToErrorQueue, "x");

        condition.Record(new EnvelopeRecord(MessageEventType.MovedToErrorQueue, new Envelope(new Message2()), 100, null));

        condition.IsCompleted().ShouldBeFalse();
    }

    [Fact]
    public async Task a_message_that_only_dead_letters_completes_the_session()
    {
        using var host = await buildHost();

        var session = await host.TrackActivity()
            .Timeout(10.Seconds())
            .DoNotAssertOnExceptionsDetected()
            .WaitForMessageToBeDeadLettered<AlwaysDeadLetters>()
            .SendMessageAndWaitAsync(new AlwaysDeadLetters());

        session.Status.ShouldBe(TrackingStatus.Completed);
        session.AllRecordsInOrder().ShouldContain(x => x.MessageEventType == MessageEventType.MovedToErrorQueue);
    }

    [Fact]
    public async Task a_message_that_is_scheduled_for_retry_completes_the_session()
    {
        using var host = await buildHost();

        var session = await host.TrackActivity()
            .Timeout(10.Seconds())
            .DoNotAssertOnExceptionsDetected()
            .WaitForMessageToBeRequeued<AlwaysRetriesLater>()
            .SendMessageAndWaitAsync(new AlwaysRetriesLater());

        session.Status.ShouldBe(TrackingStatus.Completed);
        session.Requeued.MessagesOf<AlwaysRetriesLater>().ShouldNotBeEmpty();
        session.AllRecordsInOrder().ShouldNotContain(x => x.MessageEventType == MessageEventType.MovedToErrorQueue);
    }

    private static Task<IHost> buildHost() => Host.CreateDefaultBuilder()
        .UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(DeadLetterRetryHandler));

            opts.Policies.OnException<DeadLetterRetryException>()
                .MoveToErrorQueue();
            opts.Policies.OnException<RetryLaterException>()
                .ScheduleRetry(1.Hours());
        }).StartAsync(TestContext.Current.CancellationToken);
}

public record AlwaysDeadLetters;

public record AlwaysRetriesLater;

public class DeadLetterRetryException : Exception;

public class RetryLaterException : Exception;

public static class DeadLetterRetryHandler
{
    public static void Handle(AlwaysDeadLetters message) => throw new DeadLetterRetryException();

    public static void Handle(AlwaysRetriesLater message) => throw new RetryLaterException();
}
