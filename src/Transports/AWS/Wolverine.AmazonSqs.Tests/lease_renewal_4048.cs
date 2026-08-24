using System.Collections.Concurrent;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
///     GH-4048, against LocalStack. <see cref="SqsListener" /> implements <see cref="ISupportLeaseRenewal" /> so that
///     core's lease renewal tracker can keep a queued-but-unsettled delivery invisible for as long as it sits in a
///     native-ack execution lane. These prove the SQS half of that contract does what it claims against a real
///     broker: calling <c>RenewLeasesAsync</c> keeps a message invisible past its visibility timeout, and not
///     calling it does not.
/// </summary>
/// <remarks>
///     The pair is the test. A green "handled once" on its own would also pass on a machine fast enough to finish
///     inside the visibility timeout, so the negative control -- the same run with no renewal, which must produce a
///     redelivery -- is what actually pins the behaviour.
/// </remarks>
public class lease_renewal_4048
{
    // Short enough to keep the tests quick, long enough that LocalStack's second-granularity visibility clock is
    // not the thing being measured
    private const int VisibilityTimeoutSeconds = 3;

    private static readonly TimeSpan ObservationWindow = 16.Seconds();

    private static async Task<IHost> startHost(string queueName)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoProvision();

                opts.ListenToSqsQueue(queueName, q =>
                    {
                        q.VisibilityTimeout = VisibilityTimeoutSeconds;
                        q.WaitTimeSeconds = 1;
                    })
                    .ProcessInline()
                    // The second listener is what picks up a redelivered copy while the first is still running
                    .ListenerCount(2);

                opts.PublishAllMessages().ToSqsQueue(queueName).SendInline();
            }).StartAsync();
    }

    private static async Task<int> executionsAfterObservationWindow(IHost host, Guid id)
    {
        await host.MessageBus().SendAsync(new LeasedSqsMessage(id));
        await Task.Delay(ObservationWindow, TestContext.Current.CancellationToken);

        return LeasedSqsMessageTracker.ExecutionsOf(id);
    }

    [Fact]
    public async Task renewing_the_lease_keeps_a_delivery_invisible_past_the_visibility_timeout()
    {
        LeasedSqsMessageTracker.Reset(renew: true);

        var queueName = "lease-renew-on-" + Guid.NewGuid().ToString("N")[..8];
        using var host = await startHost(queueName);
        try
        {
            var executions = await executionsAfterObservationWindow(host, Guid.NewGuid());

            executions.ShouldBe(1);
            LeasedSqsMessageTracker.RenewalCalls.ShouldBeGreaterThan(0);

            // Every renewal landed -- nothing came back in the "could not renew" subset
            LeasedSqsMessageTracker.Refused.ShouldBeEmpty();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     The negative control. Without renewal the message reappears at its visibility timeout while the first
    ///     copy is still in flight, which is exactly the duplication an un-renewed native-ack lane would produce.
    /// </summary>
    [Fact]
    public async Task without_renewal_the_delivery_is_redelivered_and_executed_again()
    {
        LeasedSqsMessageTracker.Reset(renew: false);

        var queueName = "lease-renew-off-" + Guid.NewGuid().ToString("N")[..8];
        using var host = await startHost(queueName);
        try
        {
            var executions = await executionsAfterObservationWindow(host, Guid.NewGuid());

            executions.ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     A dead receipt handle is a LOST lease, not a transient failure, and the contract says it comes back in
    ///     the result rather than as an exception. <c>ChangeMessageVisibilityBatch</c> reports per-entry failures
    ///     inside an otherwise successful response, which is what makes that shape possible -- and what stops one
    ///     dead handle from costing the rest of the batch its lease.
    /// </summary>
    [Fact]
    public async Task an_unrenewable_delivery_comes_back_in_the_result_without_taking_the_batch_with_it()
    {
        LeasedSqsMessageTracker.Reset(renew: true);
        LeasedSqsMessageTracker.ExtraEnvelope = new AmazonSqsEnvelope(new Message
        {
            MessageId = "not-a-real-message",
            ReceiptHandle = "AQEBdefinitelyNotAValidReceiptHandle"
        });

        var queueName = "lease-refused-" + Guid.NewGuid().ToString("N")[..8];
        using var host = await startHost(queueName);
        try
        {
            var executions = await executionsAfterObservationWindow(host, Guid.NewGuid());

            // The real message was still renewed, so it ran exactly once...
            executions.ShouldBe(1);

            // ...while the dead handle came back as un-renewable rather than throwing the whole batch away
            LeasedSqsMessageTracker.Refused.ShouldContain(LeasedSqsMessageTracker.ExtraEnvelope!.Id);
        }
        finally
        {
            LeasedSqsMessageTracker.ExtraEnvelope = null;
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void an_sqs_queue_declares_that_its_unsettled_deliveries_expire()
    {
        var transport = new AmazonSqsTransport();

        // The endpoint-side half of the contract: ListeningAgent uses this to refuse to start a NativeAck
        // endpoint whose listener cannot renew
        transport.Queues["anything"].holdsExpiringLease.ShouldBeTrue();
    }

    /// <summary>
    ///     GH-4019's heartbeat stays Inline-only and keeps its opt-in flag. NativeAck renewal is driven from core
    ///     through <see cref="ISupportLeaseRenewal" /> instead, because this heartbeat's Track/Untrack pair
    ///     straddles <c>ReceivedAsync</c>, which under NativeAck returns as soon as the envelope is enqueued.
    /// </summary>
    [Fact]
    public void the_inline_heartbeat_is_unchanged_by_native_ack_renewal()
    {
        var transport = new AmazonSqsTransport();

        var inline = transport.Queues["inline"];
        inline.Mode = EndpointMode.Inline;
        inline.ExtendVisibilityWhileHandling = true;
        SqsListener.ShouldExtendVisibility(inline).ShouldBeTrue();

        inline.ExtendVisibilityWhileHandling = false;
        SqsListener.ShouldExtendVisibility(inline).ShouldBeFalse();

        var buffered = transport.Queues["buffered"];
        buffered.ExtendVisibilityWhileHandling = true;
        SqsListener.ShouldExtendVisibility(buffered).ShouldBeFalse();
    }
}

public record LeasedSqsMessage(Guid Id);

public static class LeasedSqsMessageTracker
{
    private static readonly ConcurrentDictionary<Guid, int> Executions = new();

    /// <summary>How long the handler holds the delivery -- comfortably past several visibility timeouts.</summary>
    public static readonly TimeSpan HandlerDuration = 9.Seconds();

    public static volatile bool Renew;
    public static int RenewalCalls;

    /// <summary>An extra envelope carrying a dead receipt handle, mixed into the renewal batch.</summary>
    public static Envelope? ExtraEnvelope;

    public static ConcurrentBag<Guid> Refused { get; private set; } = new();

    public static void Reset(bool renew)
    {
        Executions.Clear();
        Refused = new ConcurrentBag<Guid>();
        RenewalCalls = 0;
        ExtraEnvelope = null;
        Renew = renew;
    }

    public static void Record(Guid id)
    {
        Executions.AddOrUpdate(id, 1, (_, count) => count + 1);
    }

    public static int ExecutionsOf(Guid id)
    {
        return Executions.GetValueOrDefault(id);
    }
}

public static class LeasedSqsMessageHandler
{
    /// <summary>
    ///     Counted at the start, so a second delivery that begins while the first is still running shows up inside
    ///     the observation window. The envelope is the live <c>AmazonSqsEnvelope</c> and its Listener is the
    ///     <c>SqsListener</c> that delivered it -- exactly the pair core's tracker works with.
    /// </summary>
    public static async Task Handle(LeasedSqsMessage message, Envelope envelope, CancellationToken token)
    {
        LeasedSqsMessageTracker.Record(message.Id);

        if (envelope.Listener is not ISupportLeaseRenewal renewal)
        {
            throw new InvalidOperationException(
                $"{envelope.Listener?.GetType().Name} no longer implements {nameof(ISupportLeaseRenewal)}");
        }

        var deadline = DateTimeOffset.UtcNow.Add(LeasedSqsMessageTracker.HandlerDuration);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(1.Seconds(), token);

            if (!LeasedSqsMessageTracker.Renew) continue;

            IReadOnlyList<Envelope> batch = LeasedSqsMessageTracker.ExtraEnvelope is { } extra
                ? new[] { envelope, extra }
                : new[] { envelope };

            var refused = await renewal.RenewLeasesAsync(batch, token);
            Interlocked.Increment(ref LeasedSqsMessageTracker.RenewalCalls);

            foreach (var lost in refused)
            {
                LeasedSqsMessageTracker.Refused.Add(lost.Id);
            }
        }
    }
}
