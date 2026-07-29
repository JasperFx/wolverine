using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;
using AdvisoryLock = Wolverine.Postgresql.AdvisoryLock;

namespace PostgresqlTests;

/// <summary>
/// GH-3664 — long-held exclusivity must stay invisible to Marten's async-daemon gap-liveness gate
/// (marten#4953/#5057), which counts any session with an open transaction older than an event-sequence
/// gap as a possible reserver of that gap and holds the high-water mark for it. Wolverine's dedicated
/// advisory-lock sessions live for the process lifetime, so they must always read as
/// <c>state='idle'</c> with a NULL <c>xact_start</c> in pg_stat_activity — and they carry an
/// <c>application_name</c> tag so an operator scanning pg_stat_activity can tell what is holding them.
/// </summary>
public class advisory_lock_session_hygiene : PostgresqlContext
{
    [Fact]
    public async Task lock_session_is_tagged_and_invisible_to_martens_gap_liveness_gate()
    {
        const int lockId = unchecked((int)0x3664A001);

        var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        try
        {
            var holder = new AdvisoryLock(dataSource, NullLogger.Instance, "hygiene-test");
            try
            {
                (await holder.TryAttainLockAsync(lockId, CancellationToken.None)).ShouldBeTrue();

                // Let the backend settle back to idle after the lock command.
                await Task.Delay(250, TestContext.Current.CancellationToken);

                var session = await findLockHolderSessionAsync(lockId);
                session.ShouldNotBeNull("the advisory lock must be held by a live backend");

                session!.ApplicationName.ShouldBe("wolverine-advisory-lock:hygiene-test");

                // The two properties that keep this session out of Marten's candidate-reserver set:
                // no open transaction, and plain idle state.
                session.XactStart.ShouldBeNull(
                    "the advisory-lock session must never hold an open transaction — an open transaction " +
                    "makes it a candidate event-sequence reserver and freezes async-daemon progress");
                session.State.ShouldBe("idle");
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
    public async Task application_name_is_truncated_to_postgres_limit_for_long_database_identifiers()
    {
        const int lockId = unchecked((int)0x3664A002);

        // application_name is capped at NAMEDATALEN-1 (63); Postgres truncates louder builds with a
        // warning, so AdvisoryLock truncates quietly instead.
        var longIdentifier = new string('x', 100);

        var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        try
        {
            var holder = new AdvisoryLock(dataSource, NullLogger.Instance, longIdentifier);
            try
            {
                (await holder.TryAttainLockAsync(lockId, CancellationToken.None)).ShouldBeTrue();
                await Task.Delay(250, TestContext.Current.CancellationToken);

                var session = await findLockHolderSessionAsync(lockId);
                session.ShouldNotBeNull();
                session!.ApplicationName.ShouldBe($"wolverine-advisory-lock:{longIdentifier}"[..63]);
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

    private sealed record LockHolderSession(string ApplicationName, string State, DateTime? XactStart);

    private static async Task<LockHolderSession?> findLockHolderSessionAsync(int lockId)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            select a.application_name, a.state, a.xact_start
            from pg_locks l
            join pg_stat_activity a on a.pid = l.pid
            where l.locktype = 'advisory'
              and l.granted = true
              and ((l.classid::bigint << 32) | (l.objid::bigint & x'FFFFFFFF'::bigint)) = @lockId
            limit 1";
        cmd.Parameters.AddWithValue("@lockId", (long)lockId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new LockHolderSession(
            reader.GetString(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2) ? null : reader.GetDateTime(2));
    }
}
