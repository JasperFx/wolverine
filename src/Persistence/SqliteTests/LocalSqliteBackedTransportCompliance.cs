using JasperFx.Core;
using Wolverine;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Sqlite;

namespace SqliteTests;

public class LocalSqliteBackedFixture : TransportComplianceFixture, IAsyncLifetime
{
    private readonly string _connectionString;

    public LocalSqliteBackedFixture() : base("local://one/durable".ToUri())
    {
        _connectionString = Servers.CreateInMemoryConnectionString();
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
    }
}

[Collection("sqlite")]
public class LocalSqliteBackedTransportCompliance : TransportCompliance<LocalSqliteBackedFixture>;
