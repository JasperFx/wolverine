using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Sqlite;

namespace SqliteTests.Sagas;

// GH-5665: the message history of sagas in the lightweight SQLite saga tables, where recorded_at is sortable UTC text
public class saga_history_5665 : SagaHistoryCompliance
{
    private SqliteTestDatabase? _database;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _database = Servers.CreateDatabase(nameof(saga_history_5665));
        opts.PersistMessagesWithSqlite(_database.ConnectionString);
    }

    protected override ValueTask CleanUpAsync()
    {
        _database?.Dispose();
        return ValueTask.CompletedTask;
    }
}

// GH-5665: under a SQLite database per tenant, each tenant's saga history lives in that tenant's file
public class saga_history_per_tenant_5665 : SagaHistoryMultiTenancyCompliance
{
    private SqliteTestDatabase? _main;
    private SqliteTestDatabase? _red;
    private SqliteTestDatabase? _blue;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _main = Servers.CreateDatabase("saga_history_main");
        _red = Servers.CreateDatabase("saga_history_red");
        _blue = Servers.CreateDatabase("saga_history_blue");

        opts.PersistMessagesWithSqlite(_main.ConnectionString)
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register(Red, _red.ConnectionString);
                tenants.Register(Blue, _blue.ConnectionString);
            });
    }

    protected override ValueTask CleanUpAsync()
    {
        _main?.Dispose();
        _red?.Dispose();
        _blue?.Dispose();
        return ValueTask.CompletedTask;
    }
}
