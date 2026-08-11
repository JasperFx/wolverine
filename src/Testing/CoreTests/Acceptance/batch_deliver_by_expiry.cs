using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// GH-3898 — member-level DeliverBy expiry for batched messages. The grouped batch envelope used to
/// be created with a fresh SentAt and no DeliverBy, and member expiry is only ever checked at
/// execution time — which for a batched member happens on the batch envelope, not the member. So
/// member-level DeliverWithin could never fire for any message that rode a batch (the CritterWatch#969
/// field failure: a console 40 minutes behind dutifully processed 40-minute-stale telemetry whose
/// sender had set a 10-minute expiry).
/// </summary>
public class batch_deliver_by_expiry
{
    private static Task<IHost> hostWithTriggerTime(TimeSpan triggerTime)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.BatchMessagesOf<ExpiryItem>(batching =>
                {
                    batching.TriggerTime = triggerTime;
                    batching.LocalExecutionQueueName = "expiry_items";
                });
            }).StartAsync();
    }

    [Fact]
    public async Task a_member_that_expires_awaiting_batching_is_shed_while_live_members_execute()
    {
        ExpiryItemHandler.Clear();

        // A trigger time comfortably longer than the stale member's expiry, so the member
        // passes the pipeline's execution-time check when it is handled into the batching
        // channel, then expires while it waits there for the batch to assemble
        using var host = await hostWithTriggerTime(3.Seconds());

        var stale = new ExpiryItem("stale");
        var fresh = new ExpiryItem("fresh");

        var session = await host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<ExpiryItem[]>(host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
            {
                await c.PublishAsync(stale, new DeliveryOptions { DeliverWithin = 1.Seconds() });
                await c.PublishAsync(fresh);
            }));

        // The batch that executed contains only the live member
        var batch = session.Executed.SingleMessage<ExpiryItem[]>();
        batch.ShouldBe([fresh]);

        ExpiryItemHandler.Batches.Single().ShouldBe([fresh]);
    }

    [Fact]
    public async Task an_all_expired_batch_is_discarded_without_invoking_the_handler()
    {
        ExpiryItemHandler.Clear();

        using var host = await hostWithTriggerTime(3.Seconds());

        var condition = new WaitForDiscardOf<ExpiryItem[]>();

        await host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForCondition(condition)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
            {
                await c.PublishAsync(new ExpiryItem("stale-one"),
                    new DeliveryOptions { DeliverWithin = 1.Seconds() });
                await c.PublishAsync(new ExpiryItem("stale-two"),
                    new DeliveryOptions { DeliverWithin = 1.Seconds() });
            }));

        // The expired members reached a batch terminal (the discard), but no handler ever ran
        condition.IsCompleted().ShouldBeTrue();
        ExpiryItemHandler.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_batch_envelope_carries_the_latest_member_expiry_as_a_backstop()
    {
        ExpiryItemHandler.Clear();

        using var host = await hostWithTriggerTime(250.Milliseconds());

        var earlier = DateTimeOffset.UtcNow.AddHours(1);
        var later = DateTimeOffset.UtcNow.AddHours(2);

        var session = await host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<ExpiryItem[]>(host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
            {
                await c.PublishAsync(new ExpiryItem("one"), new DeliveryOptions { DeliverBy = earlier });
                await c.PublishAsync(new ExpiryItem("two"), new DeliveryOptions { DeliverBy = later });
            }));

        // If every member can expire, the batch as a whole may expire once the LATEST member
        // expiry has passed — at that point discarding the batch can never over-shed
        session.Executed.SingleEnvelope<ExpiryItem[]>().DeliverBy.ShouldBe(later);
    }

    [Fact]
    public async Task a_batch_containing_a_never_expiring_member_gets_no_backstop()
    {
        ExpiryItemHandler.Clear();

        using var host = await hostWithTriggerTime(250.Milliseconds());

        var session = await host.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<ExpiryItem[]>(host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
            {
                await c.PublishAsync(new ExpiryItem("expiring"),
                    new DeliveryOptions { DeliverBy = DateTimeOffset.UtcNow.AddHours(1) });
                await c.PublishAsync(new ExpiryItem("immortal"));
            }));

        // A member with no DeliverBy never expires, so its batch must not either
        session.Executed.SingleEnvelope<ExpiryItem[]>().DeliverBy.ShouldBeNull();
    }

    private class WaitForDiscardOf<T> : ITrackedCondition
    {
        private bool _completed;

        public void Record(EnvelopeRecord record)
        {
            if (record.MessageEventType == MessageEventType.Discarded && record.Envelope?.Message is T)
            {
                _completed = true;
            }
        }

        public bool IsCompleted() => _completed;

        public override string ToString() => $"Wait for an envelope of {typeof(T).Name} to be discarded";
    }
}

public record ExpiryItem(string Name);

public static class ExpiryItemHandler
{
    private static readonly List<ExpiryItem[]> _batches = [];

    public static IReadOnlyList<ExpiryItem[]> Batches
    {
        get
        {
            lock (_batches)
            {
                return _batches.ToArray();
            }
        }
    }

    public static void Clear()
    {
        lock (_batches)
        {
            _batches.Clear();
        }
    }

    public static void Handle(ExpiryItem[] items)
    {
        lock (_batches)
        {
            _batches.Add(items);
        }
    }
}
