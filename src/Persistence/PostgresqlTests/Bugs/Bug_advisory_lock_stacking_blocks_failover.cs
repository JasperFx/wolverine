using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;
using AdvisoryLock = Wolverine.Postgresql.AdvisoryLock;

namespace PostgresqlTests.Bugs;

/// <summary>
/// Regression for the leader-failover stall introduced by the heartbeat
/// lease-renewal change in <c>a84d6a262</c>. After that commit
/// <c>NodeAgentController.DoHealthChecksAsync</c> calls
/// <c>TryAttainLeadershipLockAsync</c> on every tick — including ticks
/// where the leader already holds the lock. Postgres session-level
/// advisory locks STACK: "Multiple lock requests stack, so that if the
/// same resource is locked three times it must then be unlocked three
/// times to be released." The leader's lock count therefore grew by one
/// per heartbeat. The single <c>ReleaseLeadershipLockAsync</c> call during
/// <c>DisableAgentsAsync</c> / <c>stepDownAsync</c> only decremented once,
/// leaving the lock still held server-side and silently blocking
/// failover — no error logged, just a stalled election. Surfaced in
/// <c>SlowTests.SharedMemory.leadership_compliance.take_over_leader_ship_if_leader_becomes_stale</c>
/// (consistent failure on main, 0/10 in isolation; passes 3/3 with this fix).
///
/// The fix makes <see cref="AdvisoryLock.TryAttainLockAsync"/> idempotent
/// against re-entrant calls on a still-held lock — a no-op short-circuit
/// when <c>_locks</c> already contains the id and the held connection is
/// still alive. <see cref="AdvisoryLock.HasLock"/> performs the same
/// liveness check, so this also keeps the GH-2602 split-brain detection
/// behaviour intact.
/// </summary>
public class Bug_advisory_lock_stacking_blocks_failover : PostgresqlContext
{
    [Fact]
    public async Task repeated_TryAttainLockAsync_does_not_stack_so_one_release_actually_releases()
    {
        const int lockId = unchecked((int)0xDEADBEEF);

        var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        try
        {
            var holder = new AdvisoryLock(dataSource, NullLogger.Instance, "stacking-test");
            try
            {
                // Simulate ten heartbeat ticks on the same leader: every
                // DoHealthChecksAsync now calls TryAttainLeadershipLockAsync.
                // Pre-fix this stacked the Postgres advisory lock ten times.
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
                // session still held nine stacked locks; post-fix it
                // succeeds.
                var contender = new AdvisoryLock(dataSource, NullLogger.Instance, "contender");
                try
                {
                    (await contender.TryAttainLockAsync(lockId, CancellationToken.None))
                        .ShouldBeTrue(
                            "A single ReleaseLockAsync after repeated TryAttainLockAsync calls on the same session " +
                            "must fully release the Postgres advisory lock so a different node can take over.");
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
        finally
        {
            await dataSource.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryAttainLockAsync_remains_idempotent_after_lock_lost_server_side()
    {
        // Defense in depth for the GH-2602 split-brain detector. If the
        // holder's backend is killed and Postgres releases the lock
        // server-side, HasLock returns false and clears _locks. A
        // subsequent TryAttainLockAsync must NOT short-circuit on stale
        // in-memory state — it must actually re-acquire (or fail honestly).
        const int lockId = unchecked((int)0xCAFEBABE);

        var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        try
        {
            var holder = new AdvisoryLock(dataSource, NullLogger.Instance, "idempotent-after-loss-test");
            try
            {
                (await holder.TryAttainLockAsync(lockId, CancellationToken.None)).ShouldBeTrue();

                var holderPid = await findAdvisoryLockHolderPidAsync(lockId);
                holderPid.ShouldNotBeNull();
                await terminateBackendAsync(holderPid!.Value);

                // After a backend kill HasLock detects the loss and clears
                // _locks. The next TryAttainLockAsync must therefore NOT
                // short-circuit on the stale in-memory state.
                holder.HasLock(lockId).ShouldBeFalse();

                // Re-attain on a fresh connection.
                (await holder.TryAttainLockAsync(lockId, CancellationToken.None))
                    .ShouldBeTrue("After backend loss the holder should be able to re-attain on a fresh connection");
            }
            finally
            {
                await holder.DisposeAsync();
            }
        }
        finally
        {
            await dataSource.DisposeAsync();
        }
    }

    private static async Task<int?> findAdvisoryLockHolderPidAsync(int lockId)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            select pid from pg_locks
            where locktype = 'advisory'
              and granted = true
              and ((classid::bigint << 32) | (objid::bigint & x'FFFFFFFF'::bigint)) = @lockId
            limit 1";
        cmd.Parameters.AddWithValue("@lockId", (long)lockId);
        var raw = await cmd.ExecuteScalarAsync();
        return raw is int pid ? pid : null;
    }

    private static async Task terminateBackendAsync(int pid)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select pg_terminate_backend(@pid)";
        cmd.Parameters.AddWithValue("@pid", pid);
        await cmd.ExecuteScalarAsync();
        await Task.Delay(250);
    }
}
