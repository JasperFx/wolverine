using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

// GH-3236: BackgroundReceiveLoop standardizes polling-listener loops (managed task, catch->backoff->continue,
// idle delay, heartbeat, fault detection, safe teardown) and reports loop health for EndpointHealthSnapshot.
public class background_receive_loop_3236
{
    private static BackgroundReceiveLoop loop(Func<CancellationToken, Task<bool>> body, TimeSpan? idle = null)
        => new(new Uri("test://loop"), NullLogger.Instance, body, CancellationToken.None, idle ?? 20.Milliseconds());

    [Fact]
    public void status_is_not_started_before_start()
    {
        var theLoop = loop(_ => Task.FromResult(true));
        theLoop.ReceiveLoopStatus.ShouldBe(ReceiveLoopStatus.NotStarted);
        theLoop.LastReceiveLoopActivityAt.ShouldBeNull();
    }

    [Fact]
    public async Task running_and_heartbeat_advances_while_iterating()
    {
        var count = 0;
        // Idle iterations (return false) so the loop paces itself with the idle delay rather than hot-spinning.
        var theLoop = loop(_ =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(false);
        });

        theLoop.Start();
        theLoop.ReceiveLoopStatus.ShouldBe(ReceiveLoopStatus.Running);

        await waitUntil(() => count > 2);
        var first = theLoop.LastReceiveLoopActivityAt;
        first.ShouldNotBeNull();

        await waitUntil(() => count > 6);
        theLoop.LastReceiveLoopActivityAt!.Value.ShouldBeGreaterThanOrEqualTo(first.Value);

        await theLoop.DisposeAsync();
    }

    [Fact]
    public async Task exceptions_increment_consecutive_failures_keep_running_then_recover()
    {
        var fail = true;
        var theLoop = loop(_ =>
        {
            if (Volatile.Read(ref fail))
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(false);
        }, idle: 10.Milliseconds());

        theLoop.Start();
        await waitUntil(() => theLoop.ConsecutiveFailures >= 2);

        // The loop keeps running through failures (it does not silently die).
        theLoop.ReceiveLoopStatus.ShouldBe(ReceiveLoopStatus.Running);

        Volatile.Write(ref fail, false);
        await waitUntil(() => theLoop.ConsecutiveFailures == 0);

        await theLoop.DisposeAsync();
    }

    [Fact]
    public async Task a_hung_iteration_freezes_the_heartbeat()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var theLoop = loop(async token =>
        {
            entered.TrySetResult();
            // Hang until the loop is cancelled (DisposeAsync below). Token-aware so teardown is clean.
            await Task.Delay(Timeout.Infinite, token);
            return true;
        });

        theLoop.Start();
        await entered.Task.WaitAsync(5.Seconds()); // heartbeat was bumped before the iteration call
        var frozen = theLoop.LastReceiveLoopActivityAt;
        frozen.ShouldNotBeNull();

        // While the iteration is hung, the heartbeat stops advancing — this is exactly the "Accepting but not
        // consuming" signal an external monitor reads.
        await Task.Delay(100);
        theLoop.LastReceiveLoopActivityAt.ShouldBe(frozen);

        // Cancellation unblocks the hung Task.Delay; the loop observes the OCE and stops cleanly.
        await theLoop.DisposeAsync();
    }

    [Fact]
    public async Task stop_async_cancels_and_marks_stopped()
    {
        var theLoop = loop(_ => Task.FromResult(false));
        theLoop.Start();
        await Task.Delay(30);

        await theLoop.StopAsync(2.Seconds());

        theLoop.ReceiveLoopStatus.ShouldBe(ReceiveLoopStatus.Stopped);
        await theLoop.DisposeAsync();
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
