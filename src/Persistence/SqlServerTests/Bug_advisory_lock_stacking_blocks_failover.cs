using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.SqlServer.Persistence;
using Xunit;

namespace SqlServerTests;

/// <summary>
/// SQL Server companion to <c>Bug_advisory_lock_stacking_blocks_failover</c>
/// in PostgresqlTests. SQL Server session-scoped application locks
/// (sp_getapplock) are reentrant — quoting the docs: "If a lock has been
/// requested in the current transaction or by the current session,
/// sp_getapplock can be called multiple times for it ... For each request
/// that returns success ... sp_releaseapplock must also be called." The
/// heartbeat-renewal change in <c>a84d6a262</c> calls
/// <c>TryAttainLeadershipLockAsync</c> on every tick, including ticks
/// where the leader already holds the lock, so the leader's lock count
/// grows by one per heartbeat. The single <c>ReleaseLeadershipLockAsync</c>
/// call during <c>DisableAgentsAsync</c> / <c>stepDownAsync</c> only
/// decrements once, leaving the lock still held server-side and silently
/// blocking failover. The fix makes
/// <see cref="SqlServerAdvisoryLock.TryAttainLockAsync"/> idempotent
/// against re-entrant calls on a still-held lock.
/// </summary>
public class Bug_advisory_lock_stacking_blocks_failover
{
    [Fact]
    public async Task repeated_TryAttainLockAsync_does_not_stack_so_one_release_actually_releases()
    {
        const int lockId = unchecked((int)0xDEADBEEF);

        var holder = new SqlServerAdvisoryLock(
            () => new SqlConnection(Servers.SqlServerConnectionString),
            NullLogger.Instance,
            "stacking-test");
        try
        {
            // Simulate ten heartbeat ticks on the same leader: every
            // DoHealthChecksAsync now calls TryAttainLeadershipLockAsync.
            // Pre-fix this stacked the SQL Server application lock ten times.
            for (var i = 0; i < 10; i++)
            {
                (await holder.TryAttainLockAsync(lockId, CancellationToken.None))
                    .ShouldBeTrue($"holder must still report success on tick {i}");
            }

            holder.HasLock(lockId).ShouldBeTrue();

            // The leader steps down or is disabled — exactly ONE release
            // call, matching DisableAgentsAsync / stepDownAsync semantics.
            await holder.ReleaseLockAsync(lockId);

            // A would-be new leader on a different connection tries to
            // attain. Pre-fix this returned false because the holder's
            // session still held nine stacked locks; post-fix it succeeds.
            var contender = new SqlServerAdvisoryLock(
                () => new SqlConnection(Servers.SqlServerConnectionString),
                NullLogger.Instance,
                "contender");
            try
            {
                (await contender.TryAttainLockAsync(lockId, CancellationToken.None))
                    .ShouldBeTrue(
                        "A single ReleaseLockAsync after repeated TryAttainLockAsync calls on the same session " +
                        "must fully release the SQL Server application lock so a different node can take over.");
            }
            finally
            {
                await contender.ReleaseLockAsync(lockId);
                await contender.DisposeAsync();
            }
        }
        finally
        {
            await holder.DisposeAsync();
        }
    }
}
