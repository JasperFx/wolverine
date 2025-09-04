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

                            runtime.MessageTracking.Requeued(lifecycle.Envelope);
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
                                runtime.MessageTracking.MovedToErrorQueue(lifecycle.Envelope, ex);
                                await lifecycle.MoveToDeadLetterQueueAsync(ex);
                                return;
                            }

                            // Otherwise requeue up to the specified max attempts
                            if (lifecycle.Envelope.Attempts >= 5)
                            {
                                runtime.MessageTracking.MovedToErrorQueue(lifecycle.Envelope, ex);
                                await lifecycle.MoveToDeadLetterQueueAsync(ex);
                                return;
                            }

                            runtime.MessageTracking.Requeued(lifecycle.Envelope);
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
            .TrackActivity(TimeSpan.FromSeconds(4))
            .DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(new TestMessageThatFails());

        // The message should have been attempted multiple times and then discarded


        session.Requeued.MessagesOf<TestMessageThatFails>().Count().ShouldBe(3);
        session.MessageFailed.MessagesOf<TestMessageThatFails>().Count().ShouldBe(0);
        session.ExecutionFinished.MessagesOf<TestMessageThatFails>().Count().ShouldBe(4);
    }

    [Fact]
    public async Task custom_action_indefinitely_handles_multiple_attempts_until_deadlettered()
    {
        var session = await _host!
            .TrackActivity(TimeSpan.FromSeconds(4))
            .DoNotAssertOnExceptionsDetected()
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
