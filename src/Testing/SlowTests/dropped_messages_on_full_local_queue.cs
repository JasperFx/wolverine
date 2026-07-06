using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests;

/// <summary>
/// Reproduction harness for GH-3287: Wolverine silently drops cascaded messages once more than
/// 10,000 are queued into a buffered (in-memory) local queue faster than the handlers can drain it.
///
/// Handler1 handles a single StartPipeline message and cascades <see cref="MessageCount"/> Step2 messages
/// to a buffered local queue. Handler2 consumes them. Because the underlying JasperFx Block&lt;T&gt; uses a
/// bounded channel of 10,000 and the synchronous enqueue path (BufferedReceiver.Enqueue -> Block.Post ->
/// Channel.Writer.TryWrite) silently fails when the channel is full, everything past ~10,000 is lost.
/// </summary>
public class dropped_messages_on_full_local_queue
{
    public const int MessageCount = 20_000;

    private readonly ITestOutputHelper _output;

    public dropped_messages_on_full_local_queue(ITestOutputHelper output) => _output = output;

    // parallelism 5 mirrors the customer's config; parallelism 1 is the harder case — the single reader
    // that runs Handler1 is the only thing that can drain the queue, so naive back pressure (await for
    // room on a full bounded channel) would deadlock. The unbounded buffered local queue must handle both.
    [Theory]
    [InlineData(5)]
    [InlineData(1)]
    public async Task all_cascaded_messages_reach_the_second_handler(int maxParallel)
    {
        DropTestTracker.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeAssembly(typeof(dropped_messages_on_full_local_queue).Assembly);

                // Route both the trigger and the cascaded messages to one buffered local queue,
                // throttled to a small worker count so the producer easily outruns the consumer
                // and the 10k channel fills — exactly the customer's configuration.
                opts.Publish(x => x
                    .Message<StartPipeline>()
                    .Message<Step2Message>()
                    .ToLocalQueue("pipeline")
                    .MaximumParallelMessages(maxParallel));

                opts.Policies.DisableConventionalLocalRouting();
            })
            .StartAsync();

        var bus = host.MessageBus();
        await bus.PublishAsync(new StartPipeline());

        // Wait until Handler2 goes quiet (no new messages for a stretch) or an overall timeout.
        // On the buggy code the count plateaus at ~10k and stays there; a correct implementation
        // reaches MessageCount.
        var received = await DropTestTracker.WaitForQuiescenceAsync(
            expected: MessageCount,
            quietPeriod: TimeSpan.FromSeconds(5),
            overallTimeout: TimeSpan.FromMinutes(2));

        _output.WriteLine($"Handler2 received {received:N0} of {MessageCount:N0} messages " +
                          $"({DropTestTracker.UniqueCount:N0} unique ids).");

        DropTestTracker.UniqueCount.ShouldBe(MessageCount);
        received.ShouldBe(MessageCount);
    }
}

public record StartPipeline;

public record Step2Message(int Id);

[WolverineHandler]
public class DropTestHandler1
{
    public IEnumerable<object> Handle(StartPipeline message)
    {
        for (var id = 0; id < dropped_messages_on_full_local_queue.MessageCount; id++)
        {
            yield return new Step2Message(id);
        }
    }
}

[WolverineHandler]
public class DropTestHandler2
{
    public void Handle(Step2Message message)
    {
        DropTestTracker.Record(message.Id);
    }
}

public static class DropTestTracker
{
    private static ConcurrentDictionary<int, byte> _seen = new();
    private static int _count;
    private static long _lastReceivedTicks;

    public static void Reset()
    {
        _seen = new ConcurrentDictionary<int, byte>();
        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);
    }

    public static void Record(int id)
    {
        _seen.TryAdd(id, 0);
        Interlocked.Increment(ref _count);
        Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);
    }

    public static int Count => Volatile.Read(ref _count);

    public static int UniqueCount => _seen.Count;

    /// <summary>
    /// Waits until either the expected number of messages have been received, or the handler has been
    /// idle (no new messages) for <paramref name="quietPeriod"/>, or the overall timeout elapses.
    /// Returns the final received count.
    /// </summary>
    public static async Task<int> WaitForQuiescenceAsync(int expected, TimeSpan quietPeriod, TimeSpan overallTimeout)
    {
        var deadline = DateTime.UtcNow + overallTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Count >= expected)
            {
                return Count;
            }

            var idle = DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastReceivedTicks), DateTimeKind.Utc);
            if (idle > quietPeriod && Count > 0)
            {
                return Count;
            }

            await Task.Delay(250);
        }

        return Count;
    }
}
