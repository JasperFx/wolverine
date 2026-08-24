using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-4049. <see cref="EndpointMode.NativeAck" /> reuses <see cref="BatchedAzureServiceBusListener" /> unchanged --
/// its receive loop never waits on handler completion and it has no settlement policy of its own, so swapping the
/// receiver is enough. The one real gap was <see cref="ISupportNativeScheduling" />: without it a scheduled retry
/// falls through to <c>Storage.Inbox</c>, which breaks the storage-free guarantee the mode exists for (GH-3708) and
/// throws outright on a host with no persistence.
/// </summary>
public class native_ack_native_scheduling_4049 : IAsyncLifetime
{
    /// <summary>
    /// Azure Service Bus has not opted into native acks yet -- supportsNativeAck is still false on the shipping
    /// queue, so ProcessInParallelWithNativeAcks() is refused in the Mode setter. This opens only that gate, which
    /// is the last thing standing between this test and the adoption follow-up.
    /// </summary>
    internal class NativeAckCapableQueue : AzureServiceBusQueue
    {
        public NativeAckCapableQueue(AzureServiceBusTransport parent, string queueName) : base(parent, queueName)
        {
        }

        protected override bool supportsNativeAck => true;
    }

    public ValueTask InitializeAsync()
    {
        AsbNativeAckSchedulingTracking.Reset();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    private static IHostBuilder hostFor(string queueName, EndpointMode mode)
    {
        return Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseAzureServiceBusTesting().AutoProvision().AutoPurgeOnStartup();

            var transport = opts.AzureServiceBusTransport();
            transport.Queues[queueName] = new NativeAckCapableQueue(transport, queueName);

            var configuration = opts.ListenToAzureServiceBusQueue(queueName);
            if (mode == EndpointMode.NativeAck)
            {
                configuration.ProcessInParallelWithNativeAcks();
            }
            else
            {
                configuration.BufferedInMemory();
            }

            opts.PublishMessage<AsbNativeAckScheduled>().ToAzureServiceBusQueue(queueName);

            opts.OnException<AsbNativeAckDeliberateFailure>().ScheduleRetry(3.Seconds());
        });
    }

    private static IListener listenerFor(IHost host, string queueName)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var uri = runtime.Options.AzureServiceBusTransport().Queues[queueName].Uri;

        return runtime.Endpoints.ActiveListeners().OfType<ListeningAgent>().Single(x => x.Uri == uri).Listener!;
    }

    /// <summary>
    /// The mode selects the batched listener, not a new class, and that listener now answers the native scheduling
    /// question. Both halves matter: MessageContext.ReScheduleAsync only consults the listener when it implements
    /// ISupportNativeScheduling AND reports NativeSchedulingEnabled.
    /// </summary>
    [Fact]
    public async Task native_ack_uses_the_batched_listener_and_it_reschedules_natively()
    {
        using var host = await hostFor("nativeack-scheduling-shape", EndpointMode.NativeAck)
            .StartAsync(TestContext.Current.CancellationToken);

        var listener = listenerFor(host, "nativeack-scheduling-shape");

        listener.ShouldBeOfType<BatchedAzureServiceBusListener>()
            .NativeSchedulingEnabled.ShouldBeTrue();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Deliberately narrow. ReScheduleAsync prefers the LISTENER over the pipeline channel, so a listener that
    /// claimed native scheduling unconditionally would take the reschedule away from BufferedReceiver (which owns
    /// an in-memory one) and from DurableReceiver (whose envelope already has an inbox row that a republished copy
    /// would collide with). NativeAck is the only mode with no rescheduler of its own.
    /// </summary>
    [Fact]
    public async Task buffered_mode_still_leaves_scheduling_to_its_receiver()
    {
        using var host = await hostFor("buffered-scheduling-shape", EndpointMode.BufferedInMemory)
            .StartAsync(TestContext.Current.CancellationToken);

        var listener = listenerFor(host, "buffered-scheduling-shape");

        listener.ShouldBeOfType<BatchedAzureServiceBusListener>()
            .NativeSchedulingEnabled.ShouldBeFalse();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The behavioural half. A scheduled retry on a native-ack endpoint must leave the process entirely: the
    /// original delivery is settled and a copy is re-published with ScheduledEnqueueTime, so the broker holds it.
    /// Every assertion below distinguishes that from the in-memory fallback, which would re-execute the SAME
    /// Envelope instance out of Wolverine's own scheduled job store without ever touching the broker.
    /// </summary>
    [Fact]
    public async Task a_scheduled_retry_goes_back_through_the_broker()
    {
        using var host = await hostFor("nativeack-scheduling", EndpointMode.NativeAck)
            .StartAsync(TestContext.Current.CancellationToken);

        await host.MessageBus().SendAsync(new AsbNativeAckScheduled("retry me"));

        (await AsbNativeAckSchedulingTracking.SecondAttempt.Task.WaitAsync(60.Seconds(),
            TestContext.Current.CancellationToken)).ShouldBeTrue();

        var attempts = AsbNativeAckSchedulingTracking.Attempts;
        attempts.Count.ShouldBe(2);

        // A different Envelope instance: this one was built by the listener from a fresh broker delivery rather
        // than handed back by an in-process scheduled job holding the original object.
        attempts[1].Envelope.ShouldNotBeSameAs(attempts[0].Envelope);

        // ...and the broker really did assign it a new sequence number, which only happens for a message that
        // was published again.
        var first = attempts[0].Envelope.ShouldBeOfType<AzureServiceBusEnvelope>();
        var second = attempts[1].Envelope.ShouldBeOfType<AzureServiceBusEnvelope>();
        second.AzureMessage.SequenceNumber.ShouldNotBe(first.AzureMessage.SequenceNumber);

        // ...held for the requested delay rather than redelivered immediately
        (attempts[1].At - attempts[0].At).ShouldBeGreaterThan(2.Seconds());

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record AsbNativeAckScheduled(string Name);

public class AsbNativeAckDeliberateFailure : Exception
{
    public AsbNativeAckDeliberateFailure() : base("Fail the first attempt so the retry has to be scheduled")
    {
    }
}

public static class AsbNativeAckSchedulingTracking
{
    public record Attempt(Envelope Envelope, DateTimeOffset At);

    public static readonly List<Attempt> Attempts = new();

    public static TaskCompletionSource<bool> SecondAttempt { get; private set; } = new();

    public static void Reset()
    {
        lock (Attempts)
        {
            Attempts.Clear();
        }

        SecondAttempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Record(Envelope envelope)
    {
        int count;
        lock (Attempts)
        {
            Attempts.Add(new Attempt(envelope, DateTimeOffset.UtcNow));
            count = Attempts.Count;
        }

        if (count == 1)
        {
            throw new AsbNativeAckDeliberateFailure();
        }

        SecondAttempt.TrySetResult(true);
    }
}

public class AsbNativeAckScheduledHandler
{
    public static void Handle(AsbNativeAckScheduled message, Envelope envelope)
    {
        AsbNativeAckSchedulingTracking.Record(envelope);
    }
}
