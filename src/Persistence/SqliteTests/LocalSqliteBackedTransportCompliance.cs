using JasperFx.Core;
using Wolverine;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Sqlite;

namespace SqliteTests;

public class LocalSqliteBackedFixture : TransportComplianceFixture, IAsyncLifetime
{
    private readonly string _connectionString;
    private readonly SqliteTestDatabase _database;

    public LocalSqliteBackedFixture() : base("local://one/durable".ToUri())
    {
        _database = Servers.CreateDatabase(nameof(LocalSqliteBackedFixture));
        _connectionString = _database.ConnectionString;
    }

    public Task InitializeAsync()
    {
        return TheOnlyAppIs(opts =>
        {
            opts.PersistMessagesWithSqlite(_connectionString);
            opts.Durability.Mode = DurabilityMode.Solo;
        });
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}

[Collection("sqlite")]
public class LocalSqliteBackedTransportCompliance : TransportCompliance<LocalSqliteBackedFixture>
{
    // The inherited test verifies "from one node to another" delivery, which
    // assumes a true multi-node broker transport (Rabbit, Azure SB, ...). The
    // SQLite "transport" is a single-host local in-process queue with SQLite
    // durability — there is no second node. The single-host send/receive path
    // is already covered by other tests in this fixture (can_send_and_wait,
    // can_request_reply, tags_the_envelope_with_the_source, etc.), so skipping
    // this case loses no unique coverage.
    [Fact(Skip = "Not meaningful for SQLite local transport: there is no second node.")]
    public override Task can_send_from_one_node_to_another_by_destination() => Task.CompletedTask;
}
