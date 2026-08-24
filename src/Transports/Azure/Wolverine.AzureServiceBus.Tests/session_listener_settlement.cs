using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-4068. A session-enabled queue always gets an inline-style listener, because
/// buildListenerForQueue takes the RequiresSession branch before it looks at Mode. In the default
/// BufferedInMemory mode that listener is paired with a BufferedReceiver, which completes the
/// message on receipt -- so the listener's own _defer used to settle an already-settled message.
/// On a session entity that second settle comes back as SessionLockLost rather than the invalid-lock
/// error CompleteAsync swallows, so it escaped, the RetryBlock burned its attempts re-running the
/// whole lambda, and the _requeue.SendAsync on the next line never ran: the retry was silently lost.
/// </summary>
public class session_listener_settlement : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();

    [Fact]
    public async Task requeue_survives_the_buffered_receivers_settle_on_receipt()
    {
        SessionSettlementHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting().AutoProvision().AutoPurgeOnStartup();

                // No ProcessInline() on purpose -- the endpoint stays in the default
                // BufferedInMemory mode, which is what pairs the inline session listener with a
                // BufferedReceiver. Any ConfigureSessionProcessor customization is what opts this
                // endpoint into InlineAzureServiceBusSessionListener.
                opts.ListenToAzureServiceBusQueue("session-settle-requeue")
                    .ConfigureSessionProcessor(_ => { });

                opts.PublishMessage<SessionSettlementMessage>()
                    .ToAzureServiceBusQueue("session-settle-requeue");

                opts.Policies.OnException<SessionSettlementException>().Requeue(3);
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.MessageBus().SendAsync(new SessionSettlementMessage("requeue me"),
            new DeliveryOptions { GroupId = "session-1" });

        await SessionSettlementHandler.Succeeded.Task
            .WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        SessionSettlementHandler.Attempts.ShouldBe(2);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task scheduled_retry_is_delivered_on_an_inline_session_listener()
    {
        SessionSettlementHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting().AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("session-settle-scheduled")
                    // A short idle timeout makes the processor let go of the session soon after the
                    // retry copy is handled. Releasing the session releases any message left
                    // unsettled in it -- which is how an unsettled original comes back as a
                    // duplicate, and what makes the third invocation observable inside a test.
                    .ConfigureSessionProcessor(o => o.SessionIdleTimeout = 5.Seconds())
                    .ProcessInline();

                opts.PublishMessage<SessionSettlementMessage>()
                    .ToAzureServiceBusQueue("session-settle-scheduled");

                opts.Policies.OnException<SessionSettlementException>().ScheduleRetry(1.Seconds());
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.MessageBus().SendAsync(new SessionSettlementMessage("schedule me"),
            new DeliveryOptions { GroupId = "session-1" });

        // GH-4062: MoveToScheduledUntilAsync now settles the original through _defer. If that settle
        // throws instead, the copy on the next line is never sent and this never completes.
        await SessionSettlementHandler.Succeeded.Task
            .WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        // Now let the session go idle and be re-accepted. An original that was never settled
        // becomes visible again here and the handler runs a third time.
        await Task.Delay(20.Seconds(), TestContext.Current.CancellationToken);

        SessionSettlementHandler.Attempts.ShouldBe(2);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record SessionSettlementMessage(string Name);

public class SessionSettlementException() : Exception("Fail the first attempt on purpose");

public class SessionSettlementHandler
{
    private static int _attempts;

    public static TaskCompletionSource Succeeded { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static int Attempts => _attempts;

    public static void Reset()
    {
        _attempts = 0;
        Succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Handle(SessionSettlementMessage message)
    {
        if (Interlocked.Increment(ref _attempts) == 1)
        {
            throw new SessionSettlementException();
        }

        Succeeded.TrySetResult();
    }
}
