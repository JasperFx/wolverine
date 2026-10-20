using System.Collections.Concurrent;
using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

// GH-4330 gave BackgroundReceiveLoop an opt-in UsesBlockingIteration so that an iteration which blocks its thread
// (the Kafka consumer's Consume(CancellationToken)) runs on a dedicated thread instead of a pool worker. Starting the
// async loop with TaskCreationOptions.LongRunning only covered it up to its first await that did not complete
// synchronously: the loop then resumed on the pool thread that completed it, so from the second iteration on every
// blocking call held a pool worker for as long as it blocked.
public class background_receive_loop_blocking_iteration_4330
{
    private static BackgroundReceiveLoop blockingLoop(Func<CancellationToken, Task<bool>> body)
        => new(new Uri("test://blocking-loop"), NullLogger.Instance, body, CancellationToken.None, 20.Milliseconds())
        {
            UsesBlockingIteration = true
        };

    [Fact]
    public async Task the_blocking_part_of_every_iteration_runs_on_one_dedicated_thread()
    {
        var threads = new ConcurrentQueue<CallingThread>();
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var theLoop = blockingLoop(async token =>
        {
            // The synchronous part of an iteration -- Consume(token) in Kafka -- runs on whichever thread called it
            threads.Enqueue(CallingThread.Current());
            if (threads.Count >= 5)
            {
                enough.TrySetResult();
            }

            // ...and what it awaits completes on the pool, as the durable inbox insert behind ReceivedAsync does
            await Task.Delay(5, token);
            return true;
        });

        theLoop.Start();
        await enough.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var recorded = threads.ToArray();
        recorded.ShouldAllBe(thread => !thread.IsThreadPoolThread);
        recorded.Select(thread => thread.ManagedThreadId).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task failures_still_back_off_and_recover_on_the_dedicated_thread()
    {
        var fail = true;
        var threads = new ConcurrentQueue<CallingThread>();

        await using var theLoop = blockingLoop(async token =>
        {
            threads.Enqueue(CallingThread.Current());
            await Task.Delay(5, token);

            if (Volatile.Read(ref fail))
            {
                throw new InvalidOperationException("boom");
            }

            return true;
        });

        theLoop.Start();
        await waitUntil(() => theLoop.ConsecutiveFailures >= 2);
        theLoop.ReceiveLoopStatus.ShouldBe(ReceiveLoopStatus.Running);

        Volatile.Write(ref fail, false);
        await waitUntil(() => theLoop.ConsecutiveFailures == 0);

        threads.ToArray().ShouldAllBe(thread => !thread.IsThreadPoolThread);
    }

    [Fact]
    public async Task dispose_stops_an_iteration_blocked_the_way_consume_blocks()
    {
        var iterations = 0;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var theLoop = blockingLoop(async token =>
        {
            if (Interlocked.Increment(ref iterations) == 1)
            {
                await Task.Yield();
                return true;
            }

            blocked.TrySetResult();

            // Consume(token) shape: hold the calling thread until the token is cancelled, then throw
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return true;
        });

        theLoop.Start();
        await blocked.Task.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await theLoop.DisposeAsync().AsTask().WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);
        stopwatch.Elapsed.ShouldBeLessThan(2.Seconds());

        theLoop.ReceiveLoopStatus.ShouldBe(ReceiveLoopStatus.Stopped);
    }

    [Fact]
    public async Task a_loop_that_does_not_block_stays_on_the_pool()
    {
        var threads = new ConcurrentQueue<CallingThread>();

        await using var theLoop = new BackgroundReceiveLoop(new Uri("test://async-loop"), NullLogger.Instance,
            async token =>
            {
                threads.Enqueue(CallingThread.Current());
                await Task.Delay(5, token);
                return true;
            }, CancellationToken.None, 20.Milliseconds());

        theLoop.Start();
        await waitUntil(() => threads.Count >= 3);

        threads.ToArray().ShouldAllBe(thread => thread.IsThreadPoolThread);
    }

    // Captured when the iteration is called: a LongRunning thread can have exited by the time the test asserts, and
    // reading IsThreadPoolThread from a dead Thread throws.
    private record struct CallingThread(int ManagedThreadId, bool IsThreadPoolThread)
    {
        public static CallingThread Current()
            => new(Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread);
    }

    private static async Task waitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }
}
