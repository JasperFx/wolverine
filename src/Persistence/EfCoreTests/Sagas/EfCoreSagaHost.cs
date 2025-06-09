using IntegrationTests;
using JasperFx;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Sagas;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace EfCoreTests.Sagas;

public class EfCoreSagaHost : ISagaHost
{
    private IHost _host;

    public IHost BuildHost<TSaga>()
    {
        _host = WolverineHost.For(opts =>
        {
            opts.DisableConventionalDiscovery().IncludeType<TSaga>();

            opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);

            opts.Services.AddDbContext<SagaDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));

            opts.UseEntityFrameworkCoreTransactions();

            opts.PublishAllMessages().Locally();
        });

        // Watch if this hangs, might have to get fancier
        Initialize().GetAwaiter().GetResult();

        return _host;
    }

    public async Task<T> LoadState<T>(Guid id) where T : Saga
    {
        using var scope = _host.Services.CreateScope();
        
        var session = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
        return await session.FindAsync<T>(id);
    }

    public async Task<T> LoadState<T>(int id) where T : Saga
    {
        using var scope = _host.Services.CreateScope();
        
        var session = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
        return await session.FindAsync<T>(id);
    }

    public async Task<T> LoadState<T>(long id) where T : Saga
    {
        using var scope = _host.Services.CreateScope();
        
        var session = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
        return await session.FindAsync<T>(id);
    }

    public async Task<T> LoadState<T>(string id) where T : Saga
    {
        using var scope = _host.Services.CreateScope();
        
        var session = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
        return await session.FindAsync<T>(id);
    }

    public async Task Initialize()
    {
        var tables = new ISchemaObject[]
        {
            new WorkflowStateTable<Guid>("GuidWorkflowState"),
            new WorkflowStateTable<int>("IntWorkflowState"),
            new WorkflowStateTable<long>("LongWorkflowState"),
            new WorkflowStateTable<string>("StringWorkflowState")
        };

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var migration = await SchemaMigration.DetermineAsync(conn, tables);
        await new SqlServerMigrator().ApplyAllAsync(conn, migration, AutoCreate.All);

        await conn.CloseAsync();

        await _host.ResetResourceState();
    }
}