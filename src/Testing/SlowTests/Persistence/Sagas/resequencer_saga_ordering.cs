using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace SlowTests.Persistence.Sagas;

/// <summary>
/// GH-4172. Ordering coverage for <see cref="ResequencerSaga{T}"/> beyond the single reported case.
///
/// The reported failure needed a very specific shape, and it is worth being explicit about it because
/// the obvious test does NOT reproduce it: a scrambled batch on its own ([5,3,1,4,2], [4,5,1,2,3],
/// [5,4,3,2,1]) passes against the broken code. If every out-of-order message is already in Pending by
/// the time the gap is filled, there is nothing queued BEHIND the replayed message and the ordering
/// holds by accident.
///
/// The shape that actually discriminates needs BOTH:
///   1. a message seeded into Pending in an earlier transaction, and
///   2. a higher-numbered backlog sitting in the queue behind the message that fills the gap.
///
/// Then the replay -- a cascading message that does not leave the context until the current envelope
/// completes -- lands at the back, behind that backlog.
/// </summary>
public class resequencer_saga_ordering : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<TestResequencerSaga>()
                    .IncludeType<EnqueueSequencedBatchHandler>();

                opts.PublishAllMessages().ToLocalQueue("sequenced");
                opts.LocalQueue("sequenced").Sequential();
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private TestResequencerSaga LoadState(Guid id)
    {
        return _host.Services.GetRequiredService<InMemorySagaPersistor>()
            .Load<TestResequencerSaga>(id)!;
    }

    /// <summary>
    ///     Delivers every order in <paramref name="seeded" /> as its own transaction, then the whole of
    ///     <paramref name="batch" /> atomically, and asserts the saga handled 1..N in order exactly once.
    /// </summary>
    private async Task assertHandledInOrderAsync(int[] seeded, int[] batch, int timeoutMs = 60000)
    {
        var total = seeded.Length + batch.Length;
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartSequencedSaga(sagaId));

        foreach (var order in seeded)
        {
            await _host.InvokeMessageAndWaitAsync(new SequencedCommand(sagaId, order));
        }

        await _host.ExecuteAndWaitAsync(async () =>
        {
            await _host.MessageBus().PublishAsync(new EnqueueSequencedBatch(sagaId, batch));
        }, timeoutInMilliseconds: timeoutMs);

        var state = LoadState(sagaId);
        var expected = Enumerable.Range(1, total).Select(x => (int?)x).ToList();

        state.ProcessedOrders.ShouldBe(expected);
        state.ProcessedOrders.Distinct().Count().ShouldBe(total); // no message handled twice
        state.LastSequence.ShouldBe(total);
        state.Pending.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(new[] { 3 }, new[] { 1, 2, 4, 5 })]
    [InlineData(new[] { 3, 4 }, new[] { 1, 2, 5, 6 })]
    [InlineData(new[] { 2 }, new[] { 1, 3, 4 })]
    [InlineData(new[] { 2, 4 }, new[] { 1, 3, 5 })]
    [InlineData(new[] { 4 }, new[] { 1, 2, 3, 5, 6, 7 })]
    [InlineData(new[] { 2, 3 }, new[] { 1, 4, 5, 6 })]
    public async Task seeded_pending_plus_a_backlog_is_handled_in_order(int[] seeded, int[] batch)
    {
        await assertHandledInOrderAsync(seeded, batch);
    }

    /// <summary>
    ///     The cases that do NOT discriminate, kept deliberately: they passed before the fix and must
    ///     keep passing after it. Without these, a future change could "fix" ordering by breaking the
    ///     ordinary out-of-order path and nothing here would notice.
    /// </summary>
    [Theory]
    [InlineData(new[] { 5, 3, 1, 4, 2 })]
    [InlineData(new[] { 4, 5, 1, 2, 3 })]
    [InlineData(new[] { 2, 3, 4, 5, 1 })]
    [InlineData(new[] { 5, 4, 3, 2, 1 })]
    [InlineData(new[] { 1, 2, 3, 4, 5 })]
    public async Task a_scrambled_batch_alone_is_handled_in_order(int[] scrambled)
    {
        await assertHandledInOrderAsync([], scrambled);
    }

    /// <summary>
    ///     Scale. Every order 1..N is delivered exactly once, split between individually-seeded messages
    ///     and one atomic batch, shuffled deterministically so the case is reproducible from the seed.
    ///     N is well past the point where the replay chain has to walk a long backlog one message at a time.
    /// </summary>
    [Theory]
    [InlineData(20, 12345)]
    [InlineData(20, 999)]
    [InlineData(50, 7)]
    [InlineData(50, 20260827)]
    [InlineData(100, 42)]
    public async Task a_large_shuffled_sequence_is_handled_in_order(int count, int seed)
    {
        var rng = new Random(seed);
        var shuffled = Enumerable.Range(1, count).OrderBy(_ => rng.Next()).ToArray();

        // A third seeded individually so some land in Pending ahead of the batch, the rest queued at once
        var seededCount = count / 3;

        await assertHandledInOrderAsync(
            shuffled.Take(seededCount).ToArray(),
            shuffled.Skip(seededCount).ToArray(),
            timeoutMs: 120000);
    }

    /// <summary>
    ///     The worst shape for the replay chain: the entire tail is seeded into Pending and the batch
    ///     delivers only the single message that unblocks all of it, so every one of the N-1 pending
    ///     messages has to be replayed in turn.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    public async Task one_message_unblocks_an_entire_pending_tail(int count)
    {
        // Seed N..2 (descending, so nothing can be handled), then deliver 1
        var seeded = Enumerable.Range(2, count - 1).Reverse().ToArray();

        await assertHandledInOrderAsync(seeded, [1], timeoutMs: 120000);
    }

    /// <summary>
    ///     A duplicate replay of an already-handled order must not be handled a second time, and must not
    ///     disturb the sequence. This exercises the `Order &lt;= LastSequence` branch directly.
    /// </summary>
    [Fact]
    public async Task a_duplicate_of_an_already_handled_order_does_not_reorder_the_sequence()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartSequencedSaga(sagaId));

        await _host.ExecuteAndWaitAsync(async () =>
        {
            await _host.MessageBus().PublishAsync(new EnqueueSequencedBatch(sagaId, [1, 2, 3]));
        }, timeoutInMilliseconds: 30000);

        LoadState(sagaId).ProcessedOrders.ShouldBe([1, 2, 3]);

        // 2 arrives again after the fact
        await _host.InvokeMessageAndWaitAsync(new SequencedCommand(sagaId, 2));

        var state = LoadState(sagaId);
        state.LastSequence.ShouldBe(3);
        state.Pending.ShouldBeEmpty();

        // Documents current behavior: the re-delivery IS handled again (the guard cannot tell a
        // legitimate replay from a duplicate), but it does not corrupt LastSequence or Pending
        state.ProcessedOrders.ShouldBe([1, 2, 3, 2]);
    }
}
