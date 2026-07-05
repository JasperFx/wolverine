using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime.Batching;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class CoalescingMessageBatcherTests
{
    private static Envelope envelopeFor(ScoreEvent message, string? tenantId = null)
    {
        return new Envelope(message) { TenantId = tenantId };
    }

    [Fact]
    public void dedupes_by_key_last_wins_but_keeps_every_member_on_the_batch()
    {
        var batcher = new CoalescingMessageBatcher<ScoreEvent, string>(x => x.AggregateId);

        var envelopes = new[]
        {
            envelopeFor(new ScoreEvent("A", 1)),
            envelopeFor(new ScoreEvent("A", 2)),
            envelopeFor(new ScoreEvent("A", 3)),
            envelopeFor(new ScoreEvent("B", 1)),
            envelopeFor(new ScoreEvent("B", 2))
        };

        var batch = batcher.Group(envelopes).ShouldHaveSingleItem();

        // What the handler SEES: one message per key, last wins
        var seen = batch.Message.ShouldBeOfType<ScoreEvent[]>();
        seen.Length.ShouldBe(2);
        seen.Single(x => x.AggregateId == "A").Version.ShouldBe(3);
        seen.Single(x => x.AggregateId == "B").Version.ShouldBe(2);

        // What gets SETTLED: every original member envelope still rides on the batch, so inbox/outbox
        // tracking and dead-lettering are identical to a non-coalescing batch.
        batch.Batch!.Length.ShouldBe(5);
    }

    [Fact]
    public void groups_by_tenant_id_before_coalescing()
    {
        var batcher = new CoalescingMessageBatcher<ScoreEvent, string>(x => x.AggregateId);

        var envelopes = new[]
        {
            envelopeFor(new ScoreEvent("A", 1), "tenant1"),
            envelopeFor(new ScoreEvent("A", 2), "tenant2"),
            envelopeFor(new ScoreEvent("A", 3), "tenant1")
        };

        var batches = batcher.Group(envelopes).ToArray();
        batches.Length.ShouldBe(2);

        var tenant1 = batches.Single(x => x.TenantId == "tenant1");
        tenant1.Message.ShouldBeOfType<ScoreEvent[]>().ShouldHaveSingleItem().Version.ShouldBe(3);
        tenant1.Batch!.Length.ShouldBe(2);

        var tenant2 = batches.Single(x => x.TenantId == "tenant2");
        tenant2.Message.ShouldBeOfType<ScoreEvent[]>().ShouldHaveSingleItem().Version.ShouldBe(2);
        tenant2.Batch!.Length.ShouldBe(1);
    }

    [Fact]
    public void coalesce_by_rejects_a_selector_for_the_wrong_element_type()
    {
        var options = new BatchingOptions(typeof(ScoreEvent));
        Should.Throw<ArgumentOutOfRangeException>(() => options.CoalesceBy((Item x) => x.Name));
    }

    [Fact]
    public void coalesce_by_installs_a_coalescing_batcher()
    {
        var options = new BatchingOptions(typeof(ScoreEvent));
        options.CoalesceBy((ScoreEvent x) => x.AggregateId);
        options.Batcher.ShouldBeOfType<CoalescingMessageBatcher<ScoreEvent, string>>();
    }
}

public class coalescing_batch_processing_end_to_end : IAsyncLifetime
{
    private IHost theHost = null!;

    public async Task InitializeAsync()
    {
        CoalescedScoreHandler.LastBatch = null;

        #region sample_batch_coalesce_by
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.BatchMessagesOf<ScoreEvent>(batching =>
                {
                    batching.BatchSize = 500;
                    batching.TriggerTime = 1.Seconds();

                    // The handler only sees ONE ScoreEvent per AggregateId (the last one wins), instead
                    // of recomputing the same aggregate many times. Every member message still settles
                    // with the batch.
                    batching.CoalesceBy((ScoreEvent x) => x.AggregateId);
                }).Sequential();
            }).StartAsync();
        #endregion
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task handler_only_sees_the_coalesced_messages()
    {
        Func<IMessageContext, Task> publish = async c =>
        {
            await c.PublishAsync(new ScoreEvent("A", 1));
            await c.PublishAsync(new ScoreEvent("A", 2));
            await c.PublishAsync(new ScoreEvent("A", 3));
            await c.PublishAsync(new ScoreEvent("B", 1));
            await c.PublishAsync(new ScoreEvent("B", 2));
        };

        await theHost.TrackActivity()
            .WaitForMessageToBeReceivedAt<ScoreEvent[]>(theHost)
            .ExecuteAndWaitAsync(publish);

        var batch = CoalescedScoreHandler.LastBatch.ShouldNotBeNull();
        batch.Length.ShouldBe(2);
        batch.Single(x => x.AggregateId == "A").Version.ShouldBe(3);
        batch.Single(x => x.AggregateId == "B").Version.ShouldBe(2);
    }
}

public class batch_context_injection : IAsyncLifetime
{
    private IHost theHost = null!;

    public async Task InitializeAsync()
    {
        BatchContextItemHandler.LastContext = null;

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.BatchMessagesOf<ContextItem>(batching =>
                {
                    batching.BatchSize = 500;
                    batching.TriggerTime = 1.Seconds();
                }).Sequential();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task batch_context_reports_the_batch_id_and_all_members()
    {
        Func<IMessageContext, Task> publish = async c =>
        {
            await c.PublishAsync(new ContextItem("one"));
            await c.PublishAsync(new ContextItem("two"));
            await c.PublishAsync(new ContextItem("three"));
        };

        await theHost.TrackActivity()
            .WaitForMessageToBeReceivedAt<ContextItem[]>(theHost)
            .ExecuteAndWaitAsync(publish);

        var context = BatchContextItemHandler.LastContext.ShouldNotBeNull();
        context.BatchId.ShouldNotBe(Guid.Empty);
        context.Members.Count.ShouldBe(3);
    }
}

#region sample_batch_coalesce_message
public record ScoreEvent(string AggregateId, int Version);
#endregion

public static class CoalescedScoreHandler
{
    public static ScoreEvent[]? LastBatch;

    public static void Handle(ScoreEvent[] events)
    {
        LastBatch = events;
    }
}

public record ContextItem(string Name);

public static class BatchContextItemHandler
{
    public static IBatchContext? LastContext;

    #region sample_injecting_ibatchcontext
    public static void Handle(ContextItem[] items, IBatchContext batch)
    {
        // batch.BatchId correlates all the log entries for this batch;
        // batch.Members describes each original message that was grouped in.
        LastContext = batch;
    }
    #endregion
}
