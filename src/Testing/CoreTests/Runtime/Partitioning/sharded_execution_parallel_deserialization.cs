using System.Collections.Concurrent;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// GH-3900: the decompress/deserialize stage in front of the sharded execution slots runs
/// N-wide instead of single-threaded. These tests pin the two things that matter about that
/// change: (1) per-GroupId FIFO ordering into the slots is preserved byte-for-byte -- including
/// when the group can only be resolved AFTER deserialization by a message-based grouping rule --
/// and (2) the deserialization work itself actually runs concurrently.
/// </summary>
public class sharded_execution_parallel_deserialization
{
    // Simulates the real HandlerPipeline.TryDeserializeEnvelope: the envelope's Message is null
    // until this stage runs. An optional per-envelope delay stands in for decompress/parse cost
    // and, when jittered, forces out-of-order completions that would expose any unordered fan-out
    private class StubDeserializingPipeline : IHandlerPipeline
    {
        private readonly ConcurrentDictionary<Guid, object> _payloads = new();
        private readonly Func<int> _delayInMilliseconds;
        private int _active;
        private int _maxActive;

        public StubDeserializingPipeline(Func<int>? delayInMilliseconds = null)
        {
            _delayInMilliseconds = delayInMilliseconds ?? (() => 0);
        }

        public int MaxObservedConcurrency => Volatile.Read(ref _maxActive);

        public Envelope EnvelopeFor(object message, string? groupId = null)
        {
            var envelope = ObjectMother.Envelope();
            envelope.GroupId = groupId;
            _payloads[envelope.Id] = message;
            return envelope;
        }

        public Task InvokeAsync(Envelope envelope, Wolverine.Transports.IChannelCallback channel)
            => throw new NotSupportedException();

        public Task InvokeAsync(Envelope envelope, Wolverine.Transports.IChannelCallback channel,
            System.Diagnostics.Activity activity)
            => throw new NotSupportedException();

        public async ValueTask<IContinuation> TryDeserializeEnvelope(Envelope envelope)
        {
            var current = Interlocked.Increment(ref _active);
            InterlockedMax(ref _maxActive, current);

            try
            {
                var delay = _delayInMilliseconds();
                if (delay > 0)
                {
                    await Task.Delay(delay);
                }
                else
                {
                    // Still hop threads so completions can interleave
                    await Task.Yield();
                }

                envelope.Message = _payloads[envelope.Id];
                return NullContinuation.Instance;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static void InterlockedMax(ref int location, int value)
        {
            int snapshot;
            while (value > (snapshot = Volatile.Read(ref location)))
            {
                if (Interlocked.CompareExchange(ref location, value, snapshot) == snapshot) return;
            }
        }
    }

    public record OrderedCoffee(string Name, int Sequence) : ICoffee;

    private static async Task<ConcurrentDictionary<string, List<int>>> runAsync(
        StubDeserializingPipeline pipeline,
        MessagePartitioningRules rules,
        IReadOnlyList<Envelope> envelopes,
        int numberOfSlots,
        int? parallelism,
        Func<Envelope, int>? overlapProbe = null)
    {
        var received = new ConcurrentDictionary<string, List<int>>();

        var sharded = new ShardedExecutionBlock(numberOfSlots, rules, (e, _) =>
        {
            overlapProbe?.Invoke(e);
            var coffee = (OrderedCoffee)e.Message!;
            received.GetOrAdd(coffee.Name, _ => new List<int>()).Add(coffee.Sequence);
            return Task.CompletedTask;
        });

        // runtime/channel are only touched on the non-NullContinuation path, which these
        // stubbed deserializations never take
        var block = parallelism.HasValue
            ? sharded.DeserializeFirst(pipeline, null!, null!, parallelism.Value)
            : sharded.DeserializeFirst(pipeline, null!, null!);

        // Posting sequentially from one thread is what DEFINES the arrival order the
        // sharded structure promises to preserve per group
        foreach (var envelope in envelopes)
        {
            await block.PostAsync(envelope);
        }

        await block.WaitForCompletionAsync();

        return received;
    }

    [Fact]
    public async Task per_group_fifo_is_preserved_when_group_id_is_already_on_the_envelope()
    {
        // Jittered deserialization cost: with 8 parallel workers, later-arriving envelopes
        // routinely finish deserializing before earlier ones. Any unordered fan-out ahead of
        // the slot hash would scramble same-group sequences here
        var pipeline = new StubDeserializingPipeline(() => Random.Shared.Next(0, 3));
        var rules = new MessagePartitioningRules(new());

        const int groups = 10;
        const int perGroup = 200;

        var envelopes = new List<Envelope>();
        for (var i = 0; i < perGroup; i++)
        {
            for (var g = 0; g < groups; g++)
            {
                var name = $"group-{g}";
                envelopes.Add(pipeline.EnvelopeFor(new OrderedCoffee(name, i), name));
            }
        }

        var received = await runAsync(pipeline, rules, envelopes, 5, parallelism: 8);

        received.Count.ShouldBe(groups);
        foreach (var (_, sequences) in received)
        {
            sequences.ShouldBe(Enumerable.Range(0, perGroup));
        }
    }

    [Fact]
    public async Task per_group_fifo_is_preserved_when_group_is_only_resolvable_after_deserialization()
    {
        // The case that forbids hash-partitioning the deserialize stage itself: no GroupId
        // header on the wire, and the ByMessage rule can only see a group once Message is
        // populated. Ordering must still hold per group
        var pipeline = new StubDeserializingPipeline(() => Random.Shared.Next(0, 3));
        var rules = new MessagePartitioningRules(new());
        rules.ByMessage<ICoffee>(x => x.Name);

        const int groups = 10;
        const int perGroup = 200;

        var envelopes = new List<Envelope>();
        for (var i = 0; i < perGroup; i++)
        {
            for (var g = 0; g < groups; g++)
            {
                envelopes.Add(pipeline.EnvelopeFor(new OrderedCoffee($"group-{g}", i)));
            }
        }

        var received = await runAsync(pipeline, rules, envelopes, 5, parallelism: 8);

        received.Count.ShouldBe(groups);
        foreach (var (_, sequences) in received)
        {
            sequences.ShouldBe(Enumerable.Range(0, perGroup));
        }
    }

    [Fact]
    public async Task single_group_traffic_is_still_strictly_sequential_and_ordered()
    {
        var pipeline = new StubDeserializingPipeline(() => Random.Shared.Next(0, 2));
        var rules = new MessagePartitioningRules(new());

        const int count = 500;
        var envelopes = new List<Envelope>();
        for (var i = 0; i < count; i++)
        {
            envelopes.Add(pipeline.EnvelopeFor(new OrderedCoffee("the-only-group", i), "the-only-group"));
        }

        var activeExecutions = 0;
        var maxActiveExecutions = 0;

        var received = await runAsync(pipeline, rules, envelopes, 5, parallelism: 8, e =>
        {
            var current = Interlocked.Increment(ref activeExecutions);
            if (current > Volatile.Read(ref maxActiveExecutions))
            {
                Interlocked.Exchange(ref maxActiveExecutions, current);
            }

            Thread.SpinWait(100);
            return Interlocked.Decrement(ref activeExecutions);
        });

        received["the-only-group"].ShouldBe(Enumerable.Range(0, count));

        // One group -> one slot -> one worker. Parallel deserialization must never leak
        // into parallel EXECUTION of a single group
        maxActiveExecutions.ShouldBe(1);
    }

    [Fact]
    public async Task deserialization_actually_runs_in_parallel()
    {
        // A fixed per-envelope cost: serially this is 60 x 20ms = 1.2s minimum. The point is a
        // coarse "faster than serial" assertion plus a direct observation of concurrency in the
        // deserialize stage -- not a benchmark
        var pipeline = new StubDeserializingPipeline(() => 20);
        var rules = new MessagePartitioningRules(new());

        const int count = 60;
        var envelopes = new List<Envelope>();
        for (var i = 0; i < count; i++)
        {
            var name = $"group-{i % 6}";
            envelopes.Add(pipeline.EnvelopeFor(new OrderedCoffee(name, i / 6), name));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var received = await runAsync(pipeline, rules, envelopes, 5, parallelism: 8);
        stopwatch.Stop();

        received.Values.Sum(x => x.Count).ShouldBe(count);

        pipeline.MaxObservedConcurrency.ShouldBeGreaterThan(1);

        // Generous bound: 8-wide over 1.2s of serial work should land near 150-300ms.
        // Anything under 900ms proves the stage is no longer the serial choke point
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task default_overload_preserves_per_group_ordering()
    {
        // Whatever parallelism the default overload picks on this machine, the ordering
        // contract must hold
        var pipeline = new StubDeserializingPipeline(() => Random.Shared.Next(0, 2));
        var rules = new MessagePartitioningRules(new());
        rules.ByMessage<ICoffee>(x => x.Name);

        const int groups = 6;
        const int perGroup = 100;

        var envelopes = new List<Envelope>();
        for (var i = 0; i < perGroup; i++)
        {
            for (var g = 0; g < groups; g++)
            {
                envelopes.Add(pipeline.EnvelopeFor(new OrderedCoffee($"group-{g}", i)));
            }
        }

        var received = await runAsync(pipeline, rules, envelopes, 5, parallelism: null);

        received.Count.ShouldBe(groups);
        foreach (var (_, sequences) in received)
        {
            sequences.ShouldBe(Enumerable.Range(0, perGroup));
        }
    }

    [Fact]
    public async Task serial_fallback_parallelism_of_one_still_works()
    {
        var pipeline = new StubDeserializingPipeline();
        var rules = new MessagePartitioningRules(new());

        const int count = 100;
        var envelopes = new List<Envelope>();
        for (var i = 0; i < count; i++)
        {
            envelopes.Add(pipeline.EnvelopeFor(new OrderedCoffee("solo", i), "solo"));
        }

        var received = await runAsync(pipeline, rules, envelopes, 3, parallelism: 1);

        received["solo"].ShouldBe(Enumerable.Range(0, count));
    }
}
