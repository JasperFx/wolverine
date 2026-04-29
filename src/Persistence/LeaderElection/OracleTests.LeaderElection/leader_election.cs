using IntegrationTests;
using Oracle.ManagedDataAccess.Client;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.RDBMS;
using Xunit;
using Xunit.Abstractions;

namespace OracleTests.LeaderElection;

// Regression coverage for #2622 — OracleMessageStore.Initialize must register
// the OracleControlTransport / NodeControlEndpoint when running in Balanced
// durability mode, otherwise leadership election cannot start.
//
// Marked Flaky so CI does not run them by default. The compliance suite spins up
// 3-4 hosts per test and depends on TM/DML lock release between runs against a
// single Oracle instance — fine to run locally one-at-a-time, but unstable
// against the shared CI Oracle container. See #2618 (CI stabilization).
[Trait("Category", "Flaky")]
public class leader_election : LeadershipElectionCompliance
{
    public const string SchemaName = "WOLVERINE";

    // Tables Wolverine declares for the Oracle backed message store. We drop these
    // before each test run so the host's resource-setup-on-startup rebuilds a clean
    // schema. Listed children-first so CASCADE CONSTRAINTS isn't strictly required
    // (but we use it anyway as a belt-and-suspenders).
    private static readonly string[] WolverineTables =
    [
        DatabaseConstants.AgentRestrictionsTableName,
        DatabaseConstants.NodeRecordTableName,
        DatabaseConstants.TenantsTableName,
        DatabaseConstants.ControlQueueTableName,
        DatabaseConstants.NodeAssignmentsTableName,
        DatabaseConstants.NodeTableName,
        DatabaseConstants.DeadLetterTable,
        DatabaseConstants.IncomingTable,
        DatabaseConstants.OutgoingTable,
        "wolverine_locks"
    ];

    public leader_election(ITestOutputHelper output) : base(output)
    {
    }

    protected override void configureNode(WolverineOptions opts)
    {
        opts.PersistMessagesWithOracle(Servers.OracleConnectionString, SchemaName);
    }

    protected override async Task beforeBuildingHost()
    {
        // Tear down the Wolverine schema in the WOLVERINE user so each test run
        // starts from a clean slate. AddResourceSetupOnStartup in the host
        // bootstrapping will rebuild whatever it needs.
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();

        // DDL_LOCK_TIMEOUT makes DROP TABLE block (rather than fail with ORA-00054)
        // for up to N seconds when another session holds the TM lock. Combined with
        // the explicit per-table retry below, this makes the teardown resilient to a
        // prior test run whose listeners are still draining their last few queries.
        await using (var sessionCmd = conn.CreateCommand())
        {
            sessionCmd.CommandText = "ALTER SESSION SET DDL_LOCK_TIMEOUT = 30";
            await sessionCmd.ExecuteNonQueryAsync();
        }

        foreach (var table in WolverineTables)
        {
            await dropTableWithRetryAsync(conn, table);
        }

        await conn.CloseAsync();
    }

    private static async Task dropTableWithRetryAsync(OracleConnection conn, string table)
    {
        // ORA-00054 fires when a previous run's listener still holds a TM/DML lock on
        // the table. The lock releases as the prior process tears down its connections;
        // a short retry is the simplest way to ride out that window.
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    $"DROP TABLE {SchemaName}.{table.ToUpperInvariant()} CASCADE CONSTRAINTS";
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (OracleException e) when (e.Number == 942)
            {
                // ORA-00942: table or view does not exist — fine, nothing to drop.
                return;
            }
            catch (OracleException e) when (e.Number == 54 && attempt < maxAttempts)
            {
                // ORA-00054: resource busy. Wait briefly and try again.
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }
}
