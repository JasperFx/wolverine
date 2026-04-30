using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Weasel.Oracle;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace OracleTests.Agents;

// Regression coverage for the user-reported NullReferenceException at
// NodeAgentController.tryStartLeadershipAsync line 163 (`self!.AssignAgents(...)`).
//
// Root cause sits in the Oracle schema: timestamp columns are declared as
// AddColumn<DateTimeOffset>("health_check") (TIMESTAMP WITH TIME ZONE) with a
// DEFAULT of `SYS_EXTRACT_UTC(SYSTIMESTAMP)`. SYS_EXTRACT_UTC returns a TIMESTAMP
// WITHOUT time zone carrying the UTC instant. When that bare TIMESTAMP is implicitly
// cast into the TIMESTAMP WITH TIME ZONE column, Oracle stamps it with the
// SESSION time zone. For any session whose TZ is not UTC, the stored value's UTC
// equivalent is N hours off from the actual UTC instant.
//
// In production this bites NodeAgentController.DoHealthChecksAsync: the
// just-persisted current node is read back with LastHealthCheck older than
// `UtcNow - StaleNodeTimeout` (1 minute by default), gets filtered out by the
// staleness check, and `nodes.FirstOrDefault(x => x.NodeId == self.NodeId)` returns
// null. tryStartLeadershipAsync then NREs on `self!.AssignAgents([LeaderUri])`.
[Collection("oracle")]
public class health_check_timestamp_round_trip
{
    [Fact]
    public async Task just_persisted_node_must_not_be_filtered_as_stale_under_non_utc_session_tz()
    {
        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        var settings = new DatabaseSettings
        {
            SchemaName = "WOLVERINE",
            Role = MessageStoreRole.Main
        };

        var store = new OracleMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<OracleMessageStore>.Instance);
        await store.Admin.RebuildAsync();

        // Force this connection's session time zone to non-UTC so the column DEFAULT
        // expression bakes in the wrong offset — mirrors a real Oracle DB hosted in
        // a non-UTC region.
        var nodeId = Guid.NewGuid();
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            await conn.CreateCommand("ALTER SESSION SET TIME_ZONE = '+05:00'")
                .ExecuteNonQueryAsync();

            // Insert a node row using the column DEFAULT for health_check (the path
            // that NodeAgentController hits via PersistAsync on first heartbeat).
            var insertCmd = conn.CreateCommand(
                "INSERT INTO WOLVERINE.WOLVERINE_NODES (id, uri, capabilities, description, version) " +
                "VALUES (:id, :uri, :capabilities, :description, :version)");
            insertCmd.With("id", nodeId);
            insertCmd.With("uri", "tcp://localhost:1234/");
            insertCmd.With("capabilities", string.Empty);
            insertCmd.With("description", "tz-repro");
            insertCmd.With("version", "1.0");
            await insertCmd.ExecuteNonQueryAsync();
        }

        // Read back via the production API and apply the exact staleness predicate
        // NodeAgentController.DoHealthChecksAsync uses.
        var nodes = await store.Nodes.LoadAllNodesAsync(CancellationToken.None);
        var self = nodes.SingleOrDefault(x => x.NodeId == nodeId);
        self.ShouldNotBeNull("LoadAllNodesAsync did not return the just-inserted node");

        var staleTime = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(1));
        self!.LastHealthCheck.ShouldBeGreaterThan(staleTime,
            $"Just-persisted node read back as stale (would be filtered out by NodeAgentController). " +
            $"LastHealthCheck={self.LastHealthCheck:O}, UtcNow={DateTimeOffset.UtcNow:O}, " +
            $"StaleCutoff={staleTime:O}.");
    }
}
