using System.Collections.Concurrent;
using System.Diagnostics;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// GH-4010: the deserialize stage in front of the sharded slots resolves its
/// <see cref="IChannelCallback"/> per envelope rather than capturing a single one for the
/// lifetime of the block.
///
/// The channel is only consulted on the deserialization-FAILURE path -- the envelope never
/// reaches the handler pipeline, so the continuation (dead-letter, discard) has to settle the
/// delivery itself. For receivers that ack at receipt or against an inbox row, one captured
/// channel is correct and these overloads are unchanged. For a receiver whose settlement rides
/// the delivery's own transport channel it is not: a poison payload would be dead-lettered and
/// then never settled, which is a silent stall rather than an exception. With
/// <c>ListenerCount > 1</c> there isn't even a single correct channel to capture.
/// </summary>
public class sharded_execution_per_envelope_channel
{
    public record Poison(string Name);

    /// <summary>
    /// Records which envelopes were settled against it, standing in for one listener's channel.
    /// </summary>
    private class RecordingChannel : IChannelCallback
    {
        public ConcurrentBag<Guid> Completed { get; } = new();

        public IHandlerPipeline? Pipeline => null;

        public ValueTask CompleteAsync(Envelope envelope)
        {
            Completed.Add(envelope.Id);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Settles through the lifecycle, the way the real dead-letter and discard continuations do.
    /// That routes to whichever channel <c>ReadEnvelope</c> installed -- which is the thing
    /// under test.
    /// </summary>
    private class SettlingContinuation : IContinuation
    {
        public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
            DateTimeOffset now, Activity? activity)
        {
            await lifecycle.CompleteAsync();
        }
    }

    /// <summary>
    /// Every envelope "fails" deserialization by returning a non-null continuation, which is the
    /// branch that consults the channel.
    /// </summary>
    private class AlwaysFailsDeserializationPipeline : IHandlerPipeline
    {
        public Task InvokeAsync(Envelope envelope, IChannelCallback channel)
            => throw new NotSupportedException();

        public Task InvokeAsync(Envelope envelope, IChannelCallback channel, Activity activity)
            => throw new NotSupportedException();

        public async ValueTask<IContinuation> TryDeserializeEnvelope(Envelope envelope)
        {
            await Task.Yield();
            return new SettlingContinuation();
        }
    }

    [Fact]
    public async Task failed_deserialization_settles_on_the_envelopes_own_channel()
    {
        // Two channels standing in for two listeners behind one receiver -- the ListenerCount > 1
        // shape, where no single captured channel could be correct for both
        var first = new RecordingChannel();
        var second = new RecordingChannel();

        var pipeline = new AlwaysFailsDeserializationPipeline();
        var rules = new MessagePartitioningRules(new());

        var sharded = new ShardedExecutionBlock(3, rules, (_, _) => Task.CompletedTask);

        var expectedFirst = new List<Guid>();
        var expectedSecond = new List<Guid>();
        var envelopes = new List<Envelope>();
        var channelFor = new Dictionary<Guid, RecordingChannel>();

        for (var i = 0; i < 40; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.GroupId = $"group-{i % 4}";
            envelope.Message = new Poison($"poison-{i}");

            var channel = i % 2 == 0 ? first : second;
            channelFor[envelope.Id] = channel;
            (i % 2 == 0 ? expectedFirst : expectedSecond).Add(envelope.Id);

            envelopes.Add(envelope);
        }

        var block = sharded.DeserializeFirst(pipeline, new MockWolverineRuntime(),
            e => channelFor[e.Id], 8);

        foreach (var envelope in envelopes)
        {
            await block.PostAsync(envelope);
        }

        await block.WaitForCompletionAsync();

        // Each envelope settled exactly once, and on ITS channel -- not on a single captured one
        first.Completed.OrderBy(x => x).ShouldBe(expectedFirst.OrderBy(x => x));
        second.Completed.OrderBy(x => x).ShouldBe(expectedSecond.OrderBy(x => x));
    }

    [Fact]
    public async Task single_channel_overload_still_settles_every_envelope_on_that_channel()
    {
        // The delegating overload both existing receivers use. Behavior must be identical to
        // before GH-4010: one channel, every envelope
        var only = new RecordingChannel();

        var pipeline = new AlwaysFailsDeserializationPipeline();
        var rules = new MessagePartitioningRules(new());

        var sharded = new ShardedExecutionBlock(3, rules, (_, _) => Task.CompletedTask);

        var envelopes = new List<Envelope>();
        for (var i = 0; i < 25; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.GroupId = $"group-{i % 3}";
            envelope.Message = new Poison($"poison-{i}");
            envelopes.Add(envelope);
        }

        var block = sharded.DeserializeFirst(pipeline, new MockWolverineRuntime(), only, 4);

        foreach (var envelope in envelopes)
        {
            await block.PostAsync(envelope);
        }

        await block.WaitForCompletionAsync();

        only.Completed.OrderBy(x => x).ShouldBe(envelopes.Select(x => x.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task resolver_is_consulted_once_per_envelope_not_once_per_block()
    {
        var pipeline = new AlwaysFailsDeserializationPipeline();
        var rules = new MessagePartitioningRules(new());

        var sharded = new ShardedExecutionBlock(2, rules, (_, _) => Task.CompletedTask);

        var asked = new ConcurrentBag<Guid>();
        var channel = new RecordingChannel();

        var envelopes = new List<Envelope>();
        for (var i = 0; i < 30; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.GroupId = "solo";
            envelope.Message = new Poison($"poison-{i}");
            envelopes.Add(envelope);
        }

        var block = sharded.DeserializeFirst(pipeline, new MockWolverineRuntime(), e =>
        {
            asked.Add(e.Id);
            return channel;
        }, 4);

        foreach (var envelope in envelopes)
        {
            await block.PostAsync(envelope);
        }

        await block.WaitForCompletionAsync();

        asked.OrderBy(x => x).ShouldBe(envelopes.Select(x => x.Id).OrderBy(x => x));
    }
}
