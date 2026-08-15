using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// Settling a delivery tag the broker no longer recognises must not lose the message.
/// <see cref="RabbitMqChannelCallback"/> swallows the resulting
/// <c>PRECONDITION_FAILED - unknown delivery tag</c> rather than burning its retry budget, and the
/// broker redelivers whatever was never settled.
/// </summary>
/// <remarks>
/// This coverage used to live inside
/// <c>Bug_189_fails_if_there_are_many_messages_in_queue_on_startup</c>, which poisoned ~5% of a
/// thousand deliveries. That made it a chronic CI failure rather than a regression test: the broker
/// closes a channel on every bad ack, and closing a channel that still has deliveries in flight
/// races RabbitMQ.Client's own receive loop —
/// <c>Connection.ProcessFrameAsync</c> → <c>SessionManager.Lookup</c> throws
/// <c>KeyNotFoundException</c> for the channel number it just removed, and the client escalates that
/// to a library-initiated connection close (code=541). Nothing in Wolverine can catch it; it is
/// raised on the client's own <c>MainLoop</c> thread. See GH-3950.
///
/// <para>
/// So the poisoning lives here instead, at a message count low enough that the closing channel has
/// nothing in flight to race. Measured over repeated local runs this provokes the unknown-tag path
/// without provoking the connection kill, where the thousand-message version provoked it on 4 of 5
/// runs from a *single* poisoned tag.
/// </para>
/// </remarks>
public class stale_delivery_tag_settling
{
    [Fact]
    public async Task a_stale_delivery_tag_does_not_lose_the_message()
    {
        var queueName = RabbitTesting.NextQueueName();

        StaleTagHandler.Reset(poisonAt: 2);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts.PublishAllMessages().ToRabbitQueue(queueName).SendInline();
                opts.ListenToRabbitQueue(queueName).ProcessInline();
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();

        var bus = host.MessageBus();
        foreach (var id in ids)
        {
            await bus.PublishAsync(new StaleTagMessage(id));
        }

        // The poisoned delivery is never settled, so the broker redelivers it — which is the whole
        // point. Assert on distinct ids rather than a raw count for exactly that reason.
        await StaleTagHandler.WaitForAll(ids.Length, TimeSpan.FromSeconds(60));

        StaleTagHandler.HandledIds.ShouldBe(ids, ignoreOrder: true);
        StaleTagHandler.PoisonedCount.ShouldBe(1);

        // The load-bearing assertion. Poisoning the tag means the real delivery is never settled, so
        // the broker has to redeliver it — and a redelivery is the only externally visible proof that
        // the unknown-tag path was actually taken. Without this the test would still pass if
        // OverrideDeliveryTag quietly became a no-op, which is exactly how a chaos test rots.
        //
        // It has to be waited for, not read: the rejection, the channel teardown and the requeue all
        // happen after the fifth message has already been handled.
        await StaleTagHandler.WaitForRedelivery(ids.Length, TimeSpan.FromSeconds(30));

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record StaleTagMessage(Guid Id);

public static class StaleTagHandler
{
    private static TaskCompletionSource<bool> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly ConcurrentDictionary<Guid, byte> _ids = new();
    private static int _delivered;
    private static int _poisoned;
    private static int _poisonAt;
    private static int _expected;

    public static IReadOnlyCollection<Guid> HandledIds => _ids.Keys.ToArray();
    public static int PoisonedCount => Volatile.Read(ref _poisoned);
    public static int DeliveryCount => Volatile.Read(ref _delivered);

    /// <summary>
    /// Statics on a static handler survive for the life of the worker process, and the supervisor
    /// retries a failed test inside that same process — so a stale, already-completed source would
    /// let a retry "pass" without receiving anything. Same trap documented in Bug_189.
    /// </summary>
    public static void Reset(int poisonAt)
    {
        _ids.Clear();
        Interlocked.Exchange(ref _delivered, 0);
        Interlocked.Exchange(ref _poisoned, 0);
        _poisonAt = poisonAt;
        _source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static async Task WaitForAll(int expected, TimeSpan timeout)
    {
        _expected = expected;

        if (_ids.Count >= expected) return;

        var completed = await Task.WhenAny(_source.Task, Task.Delay(timeout));
        if (!ReferenceEquals(completed, _source.Task))
        {
            throw new TimeoutException(
                $"Only {_ids.Count} of {expected} distinct messages were handled within {timeout}. " +
                $"{Volatile.Read(ref _poisoned)} delivery tag(s) were poisoned.");
        }
    }

    /// <summary>
    /// Waits for the broker to redeliver whatever the poisoned settle failed to acknowledge.
    /// </summary>
    public static async Task WaitForRedelivery(int firstPassCount, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Volatile.Read(ref _delivered) > firstPassCount) return;
            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"The poisoned delivery was never redelivered within {timeout}: still {Volatile.Read(ref _delivered)} " +
            $"deliveries for {firstPassCount} messages. Either the override no longer produces an unknown " +
            $"delivery tag, or the settle path silently succeeded — in both cases this test is no longer " +
            $"covering what it claims to.");
    }

    public static void Handle(StaleTagMessage message, Envelope envelope)
    {
        // Poison exactly one delivery. The counter only reaches _poisonAt once, so a redelivery of
        // the poisoned message is not poisoned again and the test cannot loop.
        //
        // ulong.MaxValue, not 1. Tag 1 is a *real* tag: overriding to it early in a run just acks the
        // first message ahead of schedule, which the broker accepts, and nothing is ever rejected —
        // the assertion below caught exactly that and this test passed vacuously until it did. A tag
        // that was never delivered is rejected outright with
        // "PRECONDITION_FAILED - unknown delivery tag", which is the path under test.
        if (envelope is RabbitMqEnvelope rabbit && Interlocked.Increment(ref _delivered) == _poisonAt)
        {
            Interlocked.Increment(ref _poisoned);
            rabbit.OverrideDeliveryTag(ulong.MaxValue);
        }

        _ids.TryAdd(message.Id, 0);

        if (_ids.Count >= _expected)
        {
            _source.TrySetResult(true);
        }
    }
}
