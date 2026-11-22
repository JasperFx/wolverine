using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-5767 against the emulator. A requeue sends a copy back to the queue and completes the original, so the copy
/// is the only thing left: Azure Service Bus must accept it and must not drop it.
///
/// <para>The session half of the issue (a copy without a SessionId) is covered only by
/// <c>asb_requeue_sends_before_it_completes_5767</c>: the emulator accepts and delivers a message without a
/// SessionId on a session-enabled queue, which Azure Service Bus itself refuses.</para>
/// </summary>
public class requeue_keeps_the_message_5767 : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();

    [Fact]
    public async Task a_requeued_copy_is_not_swallowed_by_duplicate_detection()
    {
        Requeue5767Handler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                // The copy used to keep the original's MessageId, so the broker accepted the send and then
                // discarded it inside the detection window -- with the original already completed
                opts.ListenToAzureServiceBusQueue("requeue-dedup-5767")
                    .ConfigureQueue(q =>
                    {
                        q.RequiresDuplicateDetection = true;
                        q.DuplicateDetectionHistoryTimeWindow = 1.Minutes();
                    })
                    .ProcessInline();

                opts.PublishMessage<Requeue5767Message>().ToAzureServiceBusQueue("requeue-dedup-5767");

                opts.Policies.OnException<Requeue5767Exception>().Requeue();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.MessageBus().SendAsync(new Requeue5767Message("dedup"));

        await Requeue5767Handler.Succeeded.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        Requeue5767Handler.Attempts.ShouldBe(2);

        // The receiving side still sees one Wolverine envelope, not a new one
        Requeue5767Handler.EnvelopeIds.Distinct().ShouldHaveSingleItem();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record Requeue5767Message(string Name);

public class Requeue5767Exception() : Exception("Fail the first attempt on purpose");

public class Requeue5767Handler
{
    private static int _attempts;

    public static TaskCompletionSource Succeeded { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static int Attempts => _attempts;

    public static List<Guid> EnvelopeIds { get; } = new();

    public static void Reset()
    {
        _attempts = 0;
        lock (EnvelopeIds) EnvelopeIds.Clear();
        Succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Handle(Requeue5767Message message, Envelope envelope)
    {
        lock (EnvelopeIds) EnvelopeIds.Add(envelope.Id);

        if (Interlocked.Increment(ref _attempts) == 1)
        {
            throw new Requeue5767Exception();
        }

        Succeeded.TrySetResult();
    }
}
