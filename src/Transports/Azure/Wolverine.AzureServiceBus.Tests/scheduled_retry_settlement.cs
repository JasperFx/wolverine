using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// The existing native scheduling coverage only exercises CASCADED scheduling (context.ScheduleAsync),
/// which sends a brand new message and leaves the incoming delivery to be completed normally. A scheduled
/// RETRY of the incoming message goes through IListener.MoveToScheduledUntilAsync instead, which has to
/// settle the ORIGINAL delivery in addition to sending the scheduled copy.
/// </summary>
public class scheduled_retry_settlement : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();

    [Fact]
    public async Task inline_listener_settles_the_original_on_a_scheduled_retry()
    {
        AsbScheduledRetryHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("scheduled-retry-settle")
                    // PT5S is the emulator's minimum lock duration. If the original delivery is never
                    // settled, this is how long it takes Azure Service Bus to redeliver it -- keep it
                    // short enough that the redelivery lands inside the test rather than after it.
                    .ConfigureQueue(q => q.LockDuration = 5.Seconds())
                    .ProcessInline();

                opts.PublishMessage<AsbScheduledRetryMessage>()
                    .ToAzureServiceBusQueue("scheduled-retry-settle");

                opts.Policies.OnException<AsbScheduledRetryException>().ScheduleRetry(1.Seconds());
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.MessageBus().SendAsync(new AsbScheduledRetryMessage("only twice"));

        // 1st attempt throws, the scheduled retry copy arrives about a second later and succeeds
        await AsbScheduledRetryHandler.Succeeded.Task
            .WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        // Now wait out the queue's lock duration. If MoveToScheduledUntilAsync left the original
        // delivery unsettled, Azure Service Bus redelivers it here and the handler runs a third time.
        await Task.Delay(12.Seconds(), TestContext.Current.CancellationToken);

        AsbScheduledRetryHandler.Attempts.ShouldBe(2);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record AsbScheduledRetryMessage(string Name);

public class AsbScheduledRetryException() : Exception("Fail the first attempt on purpose");

public class AsbScheduledRetryHandler
{
    private static int _attempts;

    public static TaskCompletionSource Succeeded { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static int Attempts => _attempts;

    public static void Reset()
    {
        _attempts = 0;
        Succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Handle(AsbScheduledRetryMessage message)
    {
        if (Interlocked.Increment(ref _attempts) == 1)
        {
            throw new AsbScheduledRetryException();
        }

        Succeeded.TrySetResult();
    }
}
