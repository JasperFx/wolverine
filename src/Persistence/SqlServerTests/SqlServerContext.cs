using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.SqlServer.Persistence;

namespace SqlServerTests;

public abstract class SqlServerContext : IAsyncLifetime
{
    protected SqlServerMessageStore thePersistence;

    public async Task InitializeAsync()
    {
        var databaseSettings = new DatabaseSettings{ConnectionString = Servers.SqlServerConnectionString };
        thePersistence = new SqlServerMessageStore(
            databaseSettings, new DurabilitySettings(),
            new NullLogger<SqlServerMessageStore>(), Array.Empty<SagaTableDefinition>());
        await thePersistence.RebuildAsync();
        await initialize();
    }

    public virtual Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    protected virtual Task initialize()
    {
        return Task.CompletedTask;
    }
}