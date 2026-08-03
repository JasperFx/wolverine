using IntegrationTests;
using JasperFx.Core;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;

namespace OracleTests.Transport;

[Collection("oracle")]
public class clear_all_wolverine_storage : ClearAllWolverineStorageCompliance
{
    private const string SchemaName = "WOLVERINE";

    /// <summary>
    /// The queue tables do <b>not</b> live in <see cref="SchemaName"/>. Unlike every other provider in
    /// this compliance suite, the Oracle transport keeps them in a schema of its own —
    /// <c>OracleTransport.TransportSchemaName</c>, which defaults to <c>WOLVERINE_QUEUES</c> — while
    /// <see cref="SchemaName"/> holds only envelope storage.
    ///
    /// <para>Pinned here and passed to <c>EnableMessageTransport</c> below so that the schema the host
    /// creates the tables in and the schema <see cref="beforeHostAsync"/> cleans are the same string by
    /// construction. They were previously two independent guesses, and the cleanup was quietly aimed at
    /// the wrong one — see the note on <see cref="dropTableWithRetryAsync"/>.</para>
    /// </summary>
    private const string TransportSchemaName = "WOLVERINE_QUEUES";

    protected override async Task beforeHostAsync()
    {
        // Oracle has no cheap "drop schema" — clear whatever a prior run left behind by name.
        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        try
        {
            // DDL_LOCK_TIMEOUT makes DROP TABLE block rather than fail outright with ORA-00054 while
            // the previous test's listener still holds the TM lock. Same pairing as the Oracle
            // leader-election teardown: session-level timeout first, explicit retry underneath.
            await using (var sessionCmd = conn.CreateCommand("ALTER SESSION SET DDL_LOCK_TIMEOUT = 30"))
            {
                await sessionCmd.ExecuteNonQueryAsync();
            }

            foreach (var table in new[]
                     {
                         $"WOLVERINE_QUEUE_{QueueName.ToUpperInvariant()}",
                         $"WOLVERINE_QUEUE_{QueueName.ToUpperInvariant()}_SCHEDULED"
                     })
            {
                await dropTableWithRetryAsync(conn, table);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Drops one queue table, tolerating only the two outcomes that are actually fine.
    ///
    /// <para>GH-3763. This cleanup never once deleted a row. It dropped
    /// <c>WOLVERINE.WOLVERINE_QUEUE_RESETONE</c>, but the transport creates its tables in
    /// <see cref="TransportSchemaName"/> (<c>WOLVERINE_QUEUES</c>) — so every DROP raised ORA-00942
    /// against a table that was never there, and the old <c>catch (OracleException)</c> swallowed it
    /// as "table doesn't exist, nothing to do". It was right that nothing existed, and wrong about
    /// which schema it had looked in.</para>
    ///
    /// <para>What that leaks: this class runs serially, and
    /// <c>clear_all_async_alone_leaves_the_queue_tables_alone</c> deliberately ends with one queued
    /// and one scheduled row still present — it is the negative control for GH-3592. Every other
    /// test calls <c>ClearAllWolverineStorageAsync</c>, which on Oracle drops the queue tables
    /// outright, so the residue only survives when that one test runs immediately before another,
    /// and only then does <c>seedQueueAsync</c> see `Queued should be 1 but was 2`. Order-dependent,
    /// hence intermittent, hence read as flakiness: it reddened CIOracle on 2026-08-03 (run
    /// 30835978599) and burned this class's two retries in the run after it. Left alone the count
    /// climbs monotonically — a local repro reached 3.</para>
    ///
    /// <para>Now that the DROP resolves a table that actually exists, ORA-00054 becomes reachable
    /// for the first time: this class is the only provider in the suite that attaches a listener,
    /// and a polling listener holds a TM lock on the queue table (see the matching retry in
    /// <c>OracleQueue.TeardownAsync</c>). Hence 942 = done, 54 = wait and retry, anything else
    /// throws. Exhausting the retries throws too, rather than returning quietly: "could not drop the
    /// queue table" is a far better failure than a count assertion two calls later that names
    /// neither the lock nor the leftover rows.</para>
    /// </summary>
    private static async Task dropTableWithRetryAsync(OracleConnection conn, string table)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var cmd =
                    conn.CreateCommand($"DROP TABLE {TransportSchemaName}.{table} CASCADE CONSTRAINTS");
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (OracleException e) when (e.Number == 942)
            {
                // ORA-00942: table or view does not exist — nothing to drop, which is the goal.
                return;
            }
            catch (OracleException e) when (e.Number == 54 && attempt < maxAttempts)
            {
                // ORA-00054: resource busy. The prior host's connections are still tearing down.
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }

        throw new InvalidOperationException(
            $"Could not drop {TransportSchemaName}.{table} after {maxAttempts} attempts — it is still locked by " +
            "another session. The test would otherwise start against a table holding a previous test's rows.");
    }

    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.PersistMessagesWithOracle(Servers.OracleConnectionString, SchemaName)
            .EnableMessageTransport(t => t.TransportSchemaName(TransportSchemaName));

        // Registered through the listener rather than as a subscriber, unlike the other
        // providers: Oracle uppercases queue identifiers, but Uri.Host lowercases, so the
        // publishing.To(queue.Uri) inside ToOracleQueue() resolves a *second* endpoint over the
        // same physical tables. The polling interval keeps the listener from draining the queue
        // out from under the assertions.
        options.ListenToOracleQueue(QueueName).PollingInterval(1.Hours());
    }

    protected override ValueTask sendToQueueAsync(Envelope envelope)
    {
        return ((OracleQueue)theQueue).SendAsync(envelope);
    }
}
