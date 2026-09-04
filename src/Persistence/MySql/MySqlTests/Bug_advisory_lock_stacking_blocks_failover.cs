using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using Wolverine.MySql;

namespace MySqlTests;

/// <summary>
/// MySQL companion to <c>Bug_advisory_lock_stacking_blocks_failover</c> in
/// PostgresqlTests and SqlServerTests. MySQL named locks stack: calling
/// <c>GET_LOCK</c> on a name the session already holds succeeds and
/// increments that session's hold count, and the docs are explicit that
/// "if a lock is obtained a second time, it must be released twice."
/// Probed against the mysql:8.0 image this repo's docker-compose runs —
/// three <c>GET_LOCK</c> calls followed by one <c>RELEASE_LOCK</c> leave
/// <c>IS_FREE_LOCK</c> reporting 0.
///
/// The heartbeat-renewal change in <c>a84d6a262</c> calls
/// <c>TryAttainLeadershipLockAsync</c> on every tick, including ticks where
/// this node is already the leader, so the leader's hold count grows by one
/// per heartbeat. The single <c>ReleaseLeadershipLockAsync</c> call during
/// <c>DisableAgentsAsync</c> / <c>stepDownAsync</c> only decrements once,
/// leaving the lock held server-side — and <c>_locks</c> non-empty, so the
/// connection is never closed either. A would-be new leader can then never
/// attain, which is
/// <c>leader_election.take_over_leader_ship_if_leader_becomes_stale</c>
/// failing. The fix makes
/// <see cref="MySqlAdvisoryLock.TryAttainLockAsync"/> idempotent against
/// re-entrant calls on a still-held lock.
/// </summary>
public class Bug_advisory_lock_stacking_blocks_failover
{
    [Fact]
    public async Task repeated_TryAttainLockAsync_does_not_stack_so_one_release_actually_releases()
    {
        const int lockId = unchecked((int)0xDEADBEEF);

        await using var source = new MySqlDataSourceBuilder(Servers.MySqlConnectionString).Build();

        var holder = new MySqlAdvisoryLock(source, NullLogger.Instance, "stacking-test");
        try
        {
            // Simulate ten heartbeat ticks on the same leader: every
            // DoHealthChecksAsync now calls TryAttainLeadershipLockAsync.
            // Pre-fix this stacked the MySQL named lock ten times.
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
            var contender = new MySqlAdvisoryLock(source, NullLogger.Instance, "contender");
            try
            {
                (await contender.TryAttainLockAsync(lockId, CancellationToken.None))
                    .ShouldBeTrue(
                        "A single ReleaseLockAsync after repeated TryAttainLockAsync calls on the same session " +
                        "must fully release the MySQL named lock so a different node can take over.");
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
