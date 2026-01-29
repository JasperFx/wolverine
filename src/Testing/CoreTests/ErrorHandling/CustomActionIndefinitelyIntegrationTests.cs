using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.ErrorHandling;


public class CustomActionIndefinitelyIntegrationTests : IAsyncLifetime
{
    private IHost? _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "TestHost";

                opts.Policies.OnException<SpecialExceptionForIntegration>()
                    .CustomActionIndefinitely(async (runtime, lifecycle, ex) =>
                    {
                        if (ex is SpecialExceptionForIntegration)
                        {
                            if (lifecycle.Envelope.Attempts > 3)
                            {
                                runtime.MessageTracking.DiscardedEnvelope(lifecycle.Envelope);
                                await lifecycle.CompleteAsync();
                                return;
                            }

                            // ReScheduleAsync already calls MessageTracking.Requeued internally
                            await lifecycle.ReScheduleAsync(DateTimeOffset.Now.AddMilliseconds(10));
                        }
                    }, "Handle SpecialExceptionForIntegration with conditional discard/requeue");

                // Custom action with conditional logic based on exception properties
                opts.Policies.OnException<ConditionalException>()
                    .CustomActionIndefinitely(async (runtime, lifecycle, ex) =>
                    {
                        if (ex is ConditionalException conditionalEx)
                        {
                            // Discard immediately if marked as fatal
                            if (conditionalEx.ErrorType is ErrorType.Fatal)
                            {
                                await lifecycle.MoveToDeadLetterQueueAsync(ex);
                                // MoveToDeadLetterQueueAsync doesn't track MovedToErrorQueue,
                                // so we need to call it manually (similar to MoveToErrorQueue continuation)
                                runtime.MessageTracking.MovedToErrorQueue(lifecycle.Envelope, ex);
                                return;
                            }

                            // Otherwise requeue up to the specified max attempts
                            if (lifecycle.Envelope.Attempts >= 5)
                            {
                                await lifecycle.MoveToDeadLetterQueueAsync(ex);
                                // MoveToDeadLetterQueueAsync doesn't track MovedToErrorQueue,
                                // so we need to call it manually (similar to MoveToErrorQueue continuation)
                                runtime.MessageTracking.MovedToErrorQueue(lifecycle.Envelope, ex);
                                return;
                            }

                            // ReScheduleAsync already calls MessageTracking.Requeued internally
                            await lifecycle.ReScheduleAsync(DateTimeOffset.Now.AddMilliseconds(10));
                        }
                    }, "Handle ConditionalException with dynamic retry logic");
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task custom_action_indefinitely_handles_multiple_attempts_until_discarded()
    {
        var session = await _host!
            .TrackActivity(TimeSpan.FromSeconds(10))
            .DoNotAssertOnExceptionsDetected()
            .WaitForCondition(new WaitForDiscardedMessage<TestMessageThatFails>())
            .PublishMessageAndWaitAsync(new TestMessageThatFails());

        // The message should have been attempted multiple times and then discarded
        session.Requeued.MessagesOf<TestMessageThatFails>().Count().ShouldBe(3);
        session.MessageFailed.MessagesOf<TestMessageThatFails>().Count().ShouldBe(0);
        session.ExecutionFinished.MessagesOf<TestMessageThatFails>().Count().ShouldBe(4);
        session.Discarded.MessagesOf<TestMessageThatFails>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task custom_action_indefinitely_handles_multiple_attempts_until_deadlettered()
    {
        var session = await _host!
            .TrackActivity(TimeSpan.FromSeconds(10))
            .DoNotAssertOnExceptionsDetected()
            .WaitForCondition(new WaitForDeadLetteredMessage<ConditionalMessageThatFails>())
            .PublishMessageAndWaitAsync(new ConditionalMessageThatFails());

        session.Requeued.MessagesOf<ConditionalMessageThatFails>().Count().ShouldBe(4);
        session.MovedToErrorQueue.MessagesOf<ConditionalMessageThatFails>().Count().ShouldBe(1);
        session.ExecutionFinished.MessagesOf<ConditionalMessageThatFails>().Count().ShouldBe(5);
    }
}

public record TestMessageThatFails();

public class TestMessageThatFailsHandler
{
    public void Handle(TestMessageThatFails message)
    {
        // Always throws to trigger the error policy
        throw new SpecialExceptionForIntegration();
    }
}

public class SpecialExceptionForIntegration : Exception
{
    public int Code { get; set; }
}

public class ConditionalException : Exception
{
    public ErrorType ErrorType { get; set; }
    public DateTimeOffset? RescheduleAt { get; set; }

    public static ConditionalException Fatal => new() { ErrorType = ErrorType.Fatal };
    public static ConditionalException Transient(DateTimeOffset offset) => new() { ErrorType = ErrorType.Transient, RescheduleAt = offset};
}


public enum ErrorType
{
    Transient,
    Fatal
}

public record ConditionalMessageThatFails();

public class ConditionalMessageThatFailsHandler
{
    public void Handle(ConditionalMessageThatFails message, Envelope e)
    {

        if (e.Attempts >= 5)
            throw ConditionalException.Fatal;

        throw ConditionalException.Transient(DateTimeOffset.Now.AddMilliseconds(10 * e.Attempts));
    }
}

/// <summary>
/// Waits for a message of type T to be discarded
/// </summary>
public class WaitForDiscardedMessage<T> : ITrackedCondition
{
    private bool _found;

    public void Record(EnvelopeRecord record)
    {
        if (record.Envelope.Message is T && record.MessageEventType == MessageEventType.Discarded)
        {
            _found = true;
        }
    }

    public bool IsCompleted() => _found;
}

/// <summary>
/// Waits for a message of type T to be moved to the dead letter queue
/// </summary>
public class WaitForDeadLetteredMessage<T> : ITrackedCondition
{
    private bool _found;

    public void Record(EnvelopeRecord record)
    {
        if (record.Envelope.Message is T && record.MessageEventType == MessageEventType.MovedToErrorQueue)
        {
            _found = true;
        }
    }

    public bool IsCompleted() => _found;
}
