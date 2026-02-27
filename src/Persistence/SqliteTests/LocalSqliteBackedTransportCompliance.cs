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
public class LocalSqliteBackedTransportCompliance : TransportCompliance<LocalSqliteBackedFixture>;
