using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
/// GH-4215. A broker entity deleted underneath a running listener -- an emulator or LocalStack restarting
/// empty, an operator or IaC teardown -- used to kill that listener permanently. Every iteration exception was
/// treated the same: log at Error, back off (capped at one second), retry the same receive forever. Two
/// consequences, both observed on a real fleet: the listener never consumed again until the host restarted,
/// and the tight retry emitted ~23 error lines a second, drowning out everything else the host logged.
/// </summary>
public class receive_loop_entity_missing_4215
{
    private sealed class EntityGoneException : Exception;

    private static BackgroundReceiveLoop loop(Func<CancellationToken, Task<bool>> body,
        Func<CancellationToken, Task>? redeclare = null)
    {
        return new BackgroundReceiveLoop(new Uri("test://loop-4215"), NullLogger.Instance, body,
            CancellationToken.None, 20.Milliseconds())
        {
            IsEntityMissing = e => e is EntityGoneException,
            RedeclareAsync = redeclare
        };
    }

    [Fact]
    public async Task a_missing_entity_is_reported_as_such_rather_than_as_running()
    {
        var theLoop = loop(_ => throw new EntityGoneException());
        theLoop.Start();

        // Before this, a listener whose queue had been deleted reported Running forever -- an operator had
        // nothing to distinguish it from a healthy quiet one.
        await waitUntil(() => theLoop.ReceiveLoopStatus == ReceiveLoopStatus.EntityMissing);

        // Still alive and still trying: this is NOT Faulted, which means "stopped, needs a rebuild".
        theLoop.ReceiveLoopStatus.ShouldBe(ReceiveLoopStatus.EntityMissing);

        await theLoop.DisposeAsync();
    }

    [Fact]
    public async Task the_redeclare_hook_runs_so_a_wiped_broker_can_heal()
    {
        var declared = 0;
        var entityExists = false;

        var theLoop = loop(
            _ => entityExists ? Task.FromResult(false) : throw new EntityGoneException(),
            _ =>
            {
                Interlocked.Increment(ref declared);
                entityExists = true;
                return Task.CompletedTask;
            });

        theLoop.Start();

        await waitUntil(() => declared > 0);

        // The whole point: the application already knew how to declare the entity, because AutoProvision did
        // it at startup. Nothing simply re-ran that afterwards.
        await waitUntil(() => theLoop.ReceiveLoopStatus == ReceiveLoopStatus.Running);

        await theLoop.DisposeAsync();
    }

    /// <summary>
    /// A healed loop must stop reporting the diagnosis that got it there, or a recovered listener looks
    /// permanently broken to whatever is watching it.
    /// </summary>
    [Fact]
    public async Task recovering_clears_the_entity_missing_status()
    {
        var fail = true;
        var theLoop = loop(_ => fail ? throw new EntityGoneException() : Task.FromResult(false));

        theLoop.Start();
        await waitUntil(() => theLoop.ReceiveLoopStatus == ReceiveLoopStatus.EntityMissing);

        fail = false;
        await waitUntil(() => theLoop.ReceiveLoopStatus == ReceiveLoopStatus.Running);

        await theLoop.DisposeAsync();
    }

    /// <summary>
    /// A failure the transport does not recognize keeps the old behaviour exactly. Widening the new path to
    /// every exception would turn an ordinary transient blip into a five-second stall.
    /// </summary>
    [Fact]
    public async Task an_unclassified_failure_is_still_an_ordinary_retry()
    {
        var theLoop = loop(_ => throw new InvalidOperationException("a transient blip"));
        theLoop.Start();

        await waitUntil(() => theLoop.ConsecutiveFailures > 1);

        theLoop.ReceiveLoopStatus.ShouldBe(ReceiveLoopStatus.Running);

        await theLoop.DisposeAsync();
    }

    /// <summary>
    /// A transport that classifies but cannot re-declare -- AutoProvision off, so re-creating an entity the
    /// application never created is not Wolverine's call -- still gets the visibility and the backoff.
    /// </summary>
    [Fact]
    public async Task classification_without_a_redeclare_hook_still_reports_and_backs_off()
    {
        var theLoop = loop(_ => throw new EntityGoneException());
        theLoop.Start();

        await waitUntil(() => theLoop.ReceiveLoopStatus == ReceiveLoopStatus.EntityMissing);

        var failures = theLoop.ConsecutiveFailures;
        await Task.Delay(400.Milliseconds(), TestContext.Current.CancellationToken);

        // The 5s floor, not the 1s cap: a handful of retries in 400ms would mean the old cadence.
        theLoop.ConsecutiveFailures.ShouldBe(failures);

        await theLoop.DisposeAsync();
    }

    private static async Task waitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("Condition never became true");
    }
}
