using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;
using AdvisoryLock = Wolverine.Postgresql.AdvisoryLock;

namespace PostgresqlTests.Bugs;

/// <summary>
/// Reproducer for https://github.com/JasperFx/wolverine/issues/2602.
///
/// In <c>DurabilityMode.Balanced</c> with the PostgreSQL persistence,
/// leader election relies on a session-level Postgres advisory lock.
/// When the holder's underlying Postgres backend is terminated server-side
/// (network blip, idle-connection cull, Postgres failover, Azure flexserver
/// maintenance, <c>pg_terminate_backend</c>, etc.), Postgres releases the
/// advisory lock and another node legitimately acquires it — but the
/// original leader's in-process bookkeeping in <c>AdvisoryLock.HasLock</c>
/// continues to return <c>true</c>. Result: two nodes simultaneously
/// believe they are the leader, both run <c>EvaluateAssignmentsAsync</c>,
/// and both dispatch <c>AssignAgent</c> commands.
/// </summary>
public class Bug_split_brain_advisory_lock_state_divergence : PostgresqlContext
{
    [Fact]
    public async Task has_lock_returns_true_after_postgres_releases_it_due_to_backend_termination()
    {
        const int lockId = unchecked((int)0xB16B00B5);

        var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        try
        {
            var holder = new AdvisoryLock(dataSource, NullLogger.Instance, "split-brain-test");

            // 1. Stale leader acquires the leadership lock.
            (await holder.TryAttainLockAsync(lockId, CancellationToken.None)).ShouldBeTrue();
            holder.HasLock(lockId).ShouldBeTrue();

            // 2. Find the backend PID currently holding the lock and terminate it
            //    from a separate session — simulates a network blip, pg restart,
            //    idle-connection cull, or Azure flexserver maintenance.
            var holderPid = await findAdvisoryLockHolderPidAsync(lockId);
            holderPid.ShouldNotBeNull("Expected to find a backend holding the advisory lock");
            await terminateBackendAsync(holderPid!.Value);

            // 3. From a different connection, prove that Postgres really did
            //    release the lock — a contender now acquires it cleanly.
            var contender = new AdvisoryLock(dataSource, NullLogger.Instance, "contender");
            try
            {
                (await contender.TryAttainLockAsync(lockId, CancellationToken.None))
                    .ShouldBeTrue("Postgres should have released the lock when the holder's backend was terminated");
            }
            finally
            {
                await contender.ReleaseLockAsync(lockId);
                await contender.DisposeAsync();
            }

            // 4. THE BUG: holder.HasLock used to still return true even though
            //    it is no longer the lock holder server-side. NodeAgentController
            //    would then happily call EvaluateAssignmentsAsync on this stale
            //    leader. With the fix in this PR, HasLock now pings the held
            //    connection and detects the broken backend, drops the in-memory
            //    state, and returns false.
            holder.HasLock(lockId)
                .ShouldBeFalse(
                    "AdvisoryLock.HasLock returned true even though Postgres has released the session-level lock and another session has acquired it.");

            await holder.DisposeAsync();
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
