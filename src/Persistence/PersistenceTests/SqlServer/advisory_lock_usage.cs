using IntegrationTests;
using Microsoft.Data.SqlClient;
using Shouldly;
using Wolverine.SqlServer;
using Xunit;

namespace PersistenceTests.SqlServer;

public class advisory_lock_usage : SqlServerContext
{
    [Fact]
    public async Task explicitly_release_global_session_locks()
    {
        var settings = new SqlServerSettings();

        await using (var conn1 = new SqlConnection(Servers.SqlServerConnectionString))
        await using (var conn2 = new SqlConnection(Servers.SqlServerConnectionString))
        await using (var conn3 = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn1.OpenAsync();
            await conn2.OpenAsync();
            await conn3.OpenAsync();


            await settings.GetGlobalLockAsync(conn1, 1);


            // Cannot get the lock here
            (await settings.TryGetGlobalLockAsync(conn2, null, 1)).ShouldBeFalse();


            await settings.ReleaseGlobalLockAsync(conn1, 1);


            for (var j = 0; j < 5; j++)
            {
                if (await settings.TryGetGlobalLockAsync(conn2, null, 1))
                {
                    return;
                }

                await Task.Delay(250);
            }

            throw new Exception("Advisory lock was not released");
        }
    }

    [Fact]
    public async Task explicitly_release_global_tx_session_locks()
    {
        var settings = new SqlServerSettings();

        await using (var conn1 = new SqlConnection(Servers.SqlServerConnectionString))
        await using (var conn2 = new SqlConnection(Servers.SqlServerConnectionString))
        await using (var conn3 = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn1.OpenAsync();
            await conn2.OpenAsync();
            await conn3.OpenAsync();

            var tx1 = conn1.BeginTransaction();
            await settings.GetGlobalTxLockAsync(conn1, tx1, 2);


            // Cannot get the lock here
            var tx2 = conn2.BeginTransaction();
            (await settings.TryGetGlobalTxLockAsync(conn2, tx2, 2)).ShouldBeFalse();


            tx1.Rollback();


            for (var j = 0; j < 5; j++)
            {
                if (await settings.TryGetGlobalTxLockAsync(conn2, tx2, 2))
                {
                    tx2.Rollback();
                    return;
                }

                await Task.Delay(250);
            }

            throw new Exception("Advisory lock was not released");
        }
    }

    [Fact] // - too slow
    public async Task global_session_locks()
    {
        var settings = new SqlServerSettings();

        await using (var conn1 = new SqlConnection(Servers.SqlServerConnectionString))
        await using (var conn2 = new SqlConnection(Servers.SqlServerConnectionString))
        await using (var conn3 = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn1.OpenAsync();
            await conn2.OpenAsync();
            await conn3.OpenAsync();

            await settings.GetGlobalLockAsync(conn1, 24);


            try
            {
                // Cannot get the lock here
                (await settings.TryGetGlobalLockAsync(conn2, null, 24)).ShouldBeFalse();

                // Can get the new lock
                (await settings.TryGetGlobalLockAsync(conn3, null, 25)).ShouldBeTrue();

                // Cannot get the lock here
                (await settings.TryGetGlobalLockAsync(conn2, null, 25)).ShouldBeFalse();
            }
            finally
            {
                await settings.ReleaseGlobalLockAsync(conn1, 24);
                await settings.ReleaseGlobalLockAsync(conn3, 25);
            }
        }
    }

    [Fact] // -- too slow
    public async Task tx_session_locks()
    {
        var settings = new SqlServerSettings();

        await using (var conn1 = new SqlConnection(Servers.SqlServerConnectionString))
        await using (var conn2 = new SqlConnection(Servers.SqlServerConnectionString))
        await using (var conn3 = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn1.OpenAsync();
            await conn2.OpenAsync();
            await conn3.OpenAsync();

            var tx1 = conn1.BeginTransaction();
            await settings.GetGlobalTxLockAsync(conn1, tx1, 4);


            // Cannot get the lock here
            var tx2 = conn2.BeginTransaction();
            (await settings.TryGetGlobalTxLockAsync(conn2, tx2, 4)).ShouldBeFalse();

            // Can get the new lock
            var tx3 = conn3.BeginTransaction();
            (await settings.TryGetGlobalTxLockAsync(conn3, tx3, 5)).ShouldBeTrue();

            // Cannot get the lock here
            (await settings.TryGetGlobalTxLockAsync(conn2, tx2, 5)).ShouldBeFalse();

            tx1.Rollback();
            tx2.Rollback();
            tx3.Rollback();
        }
    }
}