using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence.Durability;

/// <summary>
/// GH-4319. The insert-side twin of GH-3711's completion coalescing. Concurrent single-envelope
/// durability writes share one batched round trip, a lone write still goes immediately, and a batch
/// that fails is retried one envelope at a time so each caller sees its own outcome.
/// </summary>
public class coalesced_envelope_storage_4319
{
    private readonly List<IReadOnlyList<Envelope>> theBatches = new();
    private readonly List<Envelope> theSingles = new();
    private readonly object theLock = new();

    private static readonly Uri theUri = new("local://durable");

    private Func<IReadOnlyList<Envelope>, Task> _batch;
    private Func<Envelope, Task> _one;

    public coalesced_envelope_storage_4319()
    {
        _batch = envelopes =>
        {
            lock (theLock) theBatches.Add(envelopes);
            return Task.CompletedTask;
        };

        _one = envelope =>
        {
            lock (theLock) theSingles.Add(envelope);
            return Task.CompletedTask;
        };
    }

    private EnvelopeStoreCoalescer coalescer(int batchSize = 100)
    {
        return new EnvelopeStoreCoalescer(e => _batch(e), e => _one(e), batchSize, theUri,
            NullLogger.Instance);
    }

    private static Envelope envelope() => ObjectMother.Envelope();

    /// <summary>
    /// Hold the first write open so everything posted meanwhile piles up behind it. That is the only
    /// way to make "batches form from concurrency alone" deterministic.
    /// </summary>
    private (TaskCompletionSource gate, TaskCompletionSource started) gateTheFirstWrite()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;

        var innerOne = _one;
        _one = async envelope =>
        {
            await innerOne(envelope);
            if (first)
            {
                first = false;
                started.TrySetResult();
                await gate.Task;
            }
        };

        return (gate, started);
    }

    [Fact]
    public async Task a_lone_write_goes_immediately_on_the_per_envelope_path()
    {
        var lone = envelope();

        await coalescer().StoreAsync(lone);

        theSingles.ShouldHaveSingleItem().ShouldBeSameAs(lone);
        theBatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task writes_that_arrive_during_a_flush_share_the_next_flush()
    {
        var theCoalescer = coalescer();
        var (gate, started) = gateTheFirstWrite();

        var first = theCoalescer.StoreAsync(envelope());
        await started.Task;

        var others = Enumerable.Range(0, 5).Select(_ => envelope()).ToArray();
        var pending = others.Select(e => theCoalescer.StoreAsync(e)).ToArray();

        // Nothing released yet, so nothing is stored
        first.IsCompleted.ShouldBeFalse();
        pending.Any(x => x.IsCompleted).ShouldBeFalse();

        gate.SetResult();
        await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The lone first write took the per-envelope path; the five that piled up went as ONE batch
        theSingles.ShouldHaveSingleItem();
        var batch = theBatches.ShouldHaveSingleItem();
        batch.Count.ShouldBe(5);
        others.ShouldAllBe(e => batch.Contains(e));
    }

    [Fact]
    public async Task store_async_does_not_return_until_the_write_has_landed()
    {
        var theCoalescer = coalescer();
        var (gate, started) = gateTheFirstWrite();

        var pending = theCoalescer.StoreAsync(envelope());
        await started.Task;

        await Task.Delay(100, TestContext.Current.CancellationToken);
        pending.IsCompleted.ShouldBeFalse("StoreAsync returned before the write finished");

        gate.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_pile_up_larger_than_the_batch_size_is_flushed_in_chunks()
    {
        var theCoalescer = coalescer(batchSize: 3);
        var (gate, started) = gateTheFirstWrite();

        var first = theCoalescer.StoreAsync(envelope());
        await started.Task;

        var pending = Enumerable.Range(0, 6).Select(_ => theCoalescer.StoreAsync(envelope())).ToArray();

        gate.SetResult();
        await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        theBatches.Count.ShouldBe(2);
        theBatches.ShouldAllBe(x => x.Count == 3);
    }

    [Fact]
    public async Task a_maximum_batch_size_of_one_never_batches()
    {
        var theCoalescer = coalescer(batchSize: 1);
        var (gate, started) = gateTheFirstWrite();

        var first = theCoalescer.StoreAsync(envelope());
        await started.Task;

        var pending = Enumerable.Range(0, 4).Select(_ => theCoalescer.StoreAsync(envelope())).ToArray();

        gate.SetResult();
        await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        theBatches.ShouldBeEmpty();
        theSingles.Count.ShouldBe(5);
    }

    [Fact]
    public async Task a_failed_batch_falls_back_to_storing_one_at_a_time()
    {
        var theCoalescer = coalescer();
        var (gate, started) = gateTheFirstWrite();
        _batch = _ => throw new InvalidOperationException("nope");

        var first = theCoalescer.StoreAsync(envelope());
        await started.Task;

        var others = Enumerable.Range(0, 4).Select(_ => envelope()).ToArray();
        var pending = others.Select(e => theCoalescer.StoreAsync(e)).ToArray();

        gate.SetResult();
        await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Every envelope still landed, one round trip each: the lone first, then the four fallbacks
        theSingles.Count.ShouldBe(5);
        others.ShouldAllBe(e => theSingles.Contains(e));
    }

    /// <summary>
    /// The behaviour that separates this from InboxCompletionCoalescer: a caller that knows how to
    /// handle its own envelope's failure -- DurableLocalQueue catching DuplicateIncomingEnvelopeException,
    /// for instance -- must keep seeing exactly that exception for exactly that envelope, and one
    /// poisoned envelope must not fail its neighbours.
    /// </summary>
    [Fact]
    public async Task a_failure_reaches_only_its_own_caller()
    {
        var theCoalescer = coalescer();
        var (gate, started) = gateTheFirstWrite();
        _batch = _ => throw new InvalidOperationException("batch always fails here");

        var first = theCoalescer.StoreAsync(envelope());
        await started.Task;

        var poison = envelope();
        var healthy = Enumerable.Range(0, 3).Select(_ => envelope()).ToArray();

        var innerOne = _one;
        _one = e => e == poison
            ? throw new DuplicateIncomingEnvelopeException(e)
            : innerOne(e);

        var poisonTask = theCoalescer.StoreAsync(poison);
        var healthyTasks = healthy.Select(e => theCoalescer.StoreAsync(e)).ToArray();

        gate.SetResult();

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => poisonTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        await Task.WhenAll(healthyTasks).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        healthyTasks.ShouldAllBe(x => x.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task drain_waits_for_the_flush_in_flight()
    {
        var theCoalescer = coalescer();
        var (gate, started) = gateTheFirstWrite();

        var pending = theCoalescer.StoreAsync(envelope());
        await started.Task;

        var drain = theCoalescer.DrainAsync();
        drain.IsCompleted.ShouldBeFalse();

        gate.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        pending.IsCompleted.ShouldBeTrue();
    }
}
