using System.Diagnostics;
using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;
using AdvisoryLock = Wolverine.Postgresql.AdvisoryLock;

namespace PostgresqlTests.Bugs;

/// <summary>
/// GH-4261. <see cref="AdvisoryLock"/> keeps one long-lived <see cref="NpgsqlConnection"/> and used to
/// synchronise access to it with nothing at all -- no semaphore, no lock, no Interlocked.
/// <c>HasLock</c> runs SYNCHRONOUS I/O on that connection (the GH-2602 liveness ping) while
/// <c>TryAttainLockAsync</c>, <c>ReleaseLockAsync</c> and <c>DisposeAsync</c> run ASYNC I/O on the same
/// field. Npgsql explicitly does not support concurrent use of one connection.
///
/// <para>The two sides are reachable together in a normal shutdown. <c>writeHeartbeats</c> and
/// <c>executeHealthChecks</c> run on the runtime-wide <c>Cancellation</c> token, which
/// <c>shutdownAsync</c> only cancels AFTER <c>teardownAgentsAsync</c> has returned; teardown "stops"
/// them with <c>Task.SafeDispose()</c>, which disposes the Task object and does nothing whatsoever to
/// the running loop. So a health check already past its own guard reaches
/// <c>ejectStaleNodes -> HasLeadershipLock() -> HasLock</c> at the same moment teardown reaches
/// <c>NodeAgentController.StopAsync -> ReleaseLeadershipLockAsync</c>.</para>
///
/// <para>Two failure modes were reported, and the tests below cover both:</para>
/// <list type="number">
/// <item>An uncancellable hang. The reader left orphaned by the interleaving never completes, and
/// <c>NpgsqlConnection.CloseAsync()</c> -- which takes no CancellationToken, so
/// <c>HostOptions.ShutdownTimeout</c> cannot reach it -- parks forever draining it. Two independent
/// process dumps caught frame-for-frame identical stacks down to every async state number. The symptom
/// is distinctive: every test passes and is counted, then the process never exits, because the hang is
/// inside collection-fixture disposal after the last test has already reported.</item>
/// <item>A spurious loss of leadership. Where Npgsql DOES notice the concurrent use it throws, and
/// <c>HasLock</c>'s catch reads any exception as "the server dropped our session", clears EVERY held
/// lock id and returns false. <c>NodeAgentController.DoHealthChecksInternalAsync</c> reads that false
/// as lost leadership and calls <c>stepDownAsync</c> -- which is exactly the churn GH-2602 and GH-3604
/// were fighting.</item>
/// </list>
///
/// <para>⚠️ These are stress tests against a real Postgres. They cannot prove a race absent; a single
/// green run is weak evidence and the iteration counts are deliberately generous.</para>
/// </summary>
public class Bug_4261_advisory_lock_connection_races : PostgresqlContext
{
    private const int LockId = unchecked((int)0xB0BCA7);

    [Fact]
    public async Task hammering_HasLock_while_attaining_and_releasing_never_corrupts_the_connection()
    {
        await using var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        var advisoryLock = new AdvisoryLock(dataSource, NullLogger.Instance, "gh4261-hammer");

        var failures = new List<Exception>();
        using var stop = new CancellationTokenSource();

        // Four pingers, standing in for the health-check tick's HasLeadershipLock() call.
        var pingers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    advisoryLock.HasLock(LockId);
                }
                catch (Exception e)
                {
                    lock (failures) failures.Add(e);
                    return;
                }
            }
        })).ToArray();

        try
        {
            // ...against the attain/release cycle the heartbeat and a stepdown drive.
            for (var i = 0; i < 60; i++)
            {
                (await advisoryLock.TryAttainLockAsync(LockId, CancellationToken.None))
                    .ShouldBeTrue($"the lock should have been attainable on round {i}");

                await advisoryLock.ReleaseLockAsync(LockId);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(pingers);
            await advisoryLock.DisposeAsync();
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_still_held_lock_is_never_reported_as_lost_while_the_connection_is_busy()
    {
        await using var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        var advisoryLock = new AdvisoryLock(dataSource, NullLogger.Instance, "gh4261-stepdown");

        (await advisoryLock.TryAttainLockAsync(LockId, CancellationToken.None)).ShouldBeTrue();

        var lostIt = 0;
        var failures = new List<Exception>();
        using var stop = new CancellationTokenSource();

        var pingers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (!advisoryLock.HasLock(LockId))
                    {
                        Interlocked.Increment(ref lostIt);
                    }
                }
                catch (Exception e)
                {
                    lock (failures) failures.Add(e);
                    return;
                }
            }
        })).ToArray();

        try
        {
            // The heartbeat renewal: TryAttainLeadershipLockAsync fires on EVERY tick, including ticks
            // where this node already holds the lock. Nothing here ever releases, so no ping has any
            // business reporting the lock lost.
            for (var i = 0; i < 60; i++)
            {
                (await advisoryLock.TryAttainLockAsync(LockId, CancellationToken.None))
                    .ShouldBeTrue($"the renewal on tick {i} should still report the lock held");
            }
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(pingers);
            await advisoryLock.DisposeAsync();
        }

        failures.ShouldBeEmpty();
        lostIt.ShouldBe(0,
            "HasLock reporting false on a lock nothing released is read as lost leadership by DoHealthChecksInternalAsync and fires stepDownAsync");
    }

    [Fact]
    public async Task disposal_completes_within_a_bound_while_HasLock_is_hammering()
    {
        await using var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        var advisoryLock = new AdvisoryLock(dataSource, NullLogger.Instance, "gh4261-dispose");

        (await advisoryLock.TryAttainLockAsync(LockId, CancellationToken.None)).ShouldBeTrue();

        using var stop = new CancellationTokenSource();
        var pingers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    advisoryLock.HasLock(LockId);
                }
                catch
                {
                    // Covered by the other tests; this one is only about whether disposal returns.
                }
            }
        })).ToArray();

        // The reported symptom was an INFINITE park inside CloseAsync, so any finite bound distinguishes
        // fixed from broken. Generous enough not to be a timing test in its own right: the gate waits
        // are 5s apiece and there is exactly one lock to release.
        var stopwatch = Stopwatch.StartNew();
        await advisoryLock.DisposeAsync();
        stopwatch.Stop();

        await stop.CancelAsync();
        await Task.WhenAll(pingers);

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }
}
