using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime.Agents;
using Wolverine.SqlServer.Persistence;
using Xunit;

namespace SqlServerTests.Agents;

// GH-3165: SQL Server has no array column type, so node capabilities are serialized to a single delimited
// string. The delimiter was a comma — but an event-subscription agent capability URI can legitimately
// contain a comma: the URI embeds a DatabaseId, and a SQL Server DatabaseId server name is "host,port"
// (e.g. "localhost,1434"). Comma-splitting on read then shredded one URI into invalid fragments and threw,
// which broke node startup under Polecat managed event-subscription distribution. This pins the round-trip.
public class node_capability_with_comma_round_trips : IAsyncLifetime
{
    private SqlServerMessageStore _store = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("node_caps");
        }

        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.SqlServerConnectionString,
            SchemaName = "node_caps",
            Role = MessageStoreRole.Main
        };

        _store = new SqlServerMessageStore(settings, new DurabilitySettings(),
            NullLogger<SqlServerMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await _store.Admin.MigrateAsync();
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    [Fact]
    public async Task persists_and_reads_a_capability_uri_that_contains_a_comma()
    {
        // The exact shape that broke Polecat managed distribution on SQL Server: the DatabaseId server
        // segment carries the "host,port" form, so the agent URI contains a comma.
        var commaUri = new Uri("event-subscriptions://polecat/main/localhost,1434.master/trip/all");
        var plainUri = new Uri("event-subscriptions://polecat/main/localhost,1434.master/day/all");

        var node = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            ControlUri = new Uri("tcp://localhost:5000"),
            Description = "comma-caps-test",
            Version = new Version(1, 0, 0)
        };
        node.Capabilities.Add(commaUri);
        node.Capabilities.Add(plainUri);

        await _store.Nodes.PersistAsync(node, CancellationToken.None);

        var loaded = (await _store.Nodes.LoadAllNodesAsync(CancellationToken.None)).Single();

        loaded.Capabilities.ShouldContain(commaUri);
        loaded.Capabilities.ShouldContain(plainUri);
        loaded.Capabilities.Count.ShouldBe(2);
    }
}
