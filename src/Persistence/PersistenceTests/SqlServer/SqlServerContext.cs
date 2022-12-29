using System.Threading.Tasks;
using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using Xunit;

namespace PersistenceTests.SqlServer;

[Collection("sqlserver")]
public abstract class SqlServerContext : IAsyncLifetime
{
    protected SqlServerMessageMessageStore thePersistence;

    public async Task InitializeAsync()
    {
        thePersistence = new SqlServerMessageMessageStore(
            new SqlServerSettings { ConnectionString = Servers.SqlServerConnectionString }, new NodeSettings(null),
            new NullLogger<SqlServerMessageMessageStore>());
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