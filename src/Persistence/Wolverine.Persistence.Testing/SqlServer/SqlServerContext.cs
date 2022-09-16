using System.Threading.Tasks;
using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Persistence.SqlServer;
using Wolverine.Persistence.SqlServer.Persistence;
using Xunit;

namespace Wolverine.Persistence.Testing.SqlServer;

[Collection("sqlserver")]
public abstract class SqlServerContext : IAsyncLifetime
{
    protected SqlServerEnvelopePersistence thePersistence;

    public async Task InitializeAsync()
    {
        thePersistence = new SqlServerEnvelopePersistence(
            new SqlServerSettings { ConnectionString = Servers.SqlServerConnectionString }, new AdvancedSettings(null),
            new NullLogger<SqlServerEnvelopePersistence>());
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
