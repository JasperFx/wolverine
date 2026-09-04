using System.Diagnostics;
using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace OracleTests.Agents;

/// <summary>
/// GH-4261 follow-up. <c>OracleAdvisoryLock.TryAttainLockAsync</c> opens a connection and only gave it
/// back on two of its exits: the success path, where the connection is retained in <c>_heldLocks</c>,
/// and the ORA-00054 contention path, which closes and disposes it. Every other failure fell through to
/// the outer <c>catch</c>, which logged and returned false with the connection -- and, once
/// <c>BeginTransactionAsync</c> had run, an open transaction on it -- still held by nobody.
///
/// <para>That is not a once-per-process cost. <c>TryAttainLeadershipLockAsync</c> fires on every
/// health-check tick, so any failure mode that persists rather than resolving -- the lock table's schema
/// missing, credentials expired, a RAC node gone -- leaks one connection per tick until the pool is
/// exhausted, at which point the node cannot open a connection for anything else either.</para>
///
/// <para>The pool ceiling here is deliberately tiny so the leak has to show. Against the unfixed code
/// the fourth attain finds the pool empty and blocks until <c>Connection Timeout</c>; against the fix
/// every attain gets a pooled connection straight back.</para>
/// </summary>
public class Bug_4261_failed_attain_returns_its_connection : OracleContext
{
    private const int LockId = unchecked((int)0xB0BCA7);
    private const string SchemaName = "WOLVERINE";

    // Comfortably more attains than the pool can hold, so a leak cannot be absorbed.
    private const int PoolCeiling = 3;
    private const int Attempts = 6;
    private static readonly TimeSpan PoolWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task a_failing_attain_does_not_leak_its_connection()
    {
        var builder = new OracleConnectionStringBuilder(Servers.OracleConnectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = PoolCeiling,
            ConnectionTimeout = (int)PoolWait.TotalSeconds
        };

        await using var dataSource = new OracleDataSource(builder.ConnectionString);

        // Nothing has ever created this schema, so the FOR UPDATE NOWAIT raises ORA-00942 -- not
        // ORA-00054 -- and lands in the outer catch. Any other durable failure gets there the same way;
        // a missing schema is just the one a test can arrange without breaking the server.
        var advisoryLock = new OracleAdvisoryLock(dataSource, NullLogger.Instance, "gh4261_no_such_schema");

        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < Attempts; i++)
        {
            (await advisoryLock.TryAttainLockAsync(LockId, CancellationToken.None))
                .ShouldBeFalse($"attain {i} was against a schema that does not exist");
        }

        stopwatch.Stop();

        // A leaked connection is not returned, so from attempt PoolCeiling on, OpenConnectionAsync sits
        // on an empty pool for the whole ConnectionTimeout before throwing. Finishing well inside even
        // one of those waits is the evidence that no attempt ever queued for a connection.
        stopwatch.Elapsed.ShouldBeLessThan(PoolWait,
            $"{Attempts} failed attains against a pool of {PoolCeiling} only stay this quick if each one gave its connection back");

        // And the pool still has everything it started with: this would time out and throw against the
        // leak, whatever the timings above happened to measure.
        var connections = new List<OracleConnection>();
        try
        {
            for (var i = 0; i < PoolCeiling; i++)
            {
                connections.Add(await dataSource.OpenConnectionAsync(CancellationToken.None));
            }
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }

        connections.Count.ShouldBe(PoolCeiling);

        await advisoryLock.DisposeAsync();
    }

    [Fact]
    public async Task an_attained_lock_keeps_its_connection()
    {
        // The other half of the same change: on success rememberId takes ownership, and the discard must
        // not touch what it now holds. HasLock only answers true by pinging that retained connection, so
        // a lock disposed out from under itself would report false here.
        await using var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.OracleConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        await using var store = new OracleMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<OracleMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await store.Admin.MigrateAsync();

        var advisoryLock = new OracleAdvisoryLock(dataSource, NullLogger.Instance, SchemaName);

        try
        {
            (await advisoryLock.TryAttainLockAsync(LockId, CancellationToken.None)).ShouldBeTrue();
            advisoryLock.HasLock(LockId).ShouldBeTrue();

            // Not asserted here: the renewal TryAttainLeadershipLockAsync drives on every tick answers
            // FALSE on Oracle, because the row lock the first attain took on its own connection blocks
            // the second attain's SELECT ... FOR UPDATE NOWAIT with ORA-00054. That path disposes its
            // connection correctly, so it is not this leak -- but it is a real difference from the
            // sibling providers, where the renewal is re-entrant and reports true.
        }
        finally
        {
            await advisoryLock.DisposeAsync();
        }
    }
}
