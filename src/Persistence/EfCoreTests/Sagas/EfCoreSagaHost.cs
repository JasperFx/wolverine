using IntegrationTests;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Sagas;
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

            opts.Services.AddDbContextWithWolverineIntegration<SagaDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));

            opts.UseEntityFrameworkCoreTransactions();
            opts.UseEntityFrameworkCoreWolverineManagedMigrations();

            opts.PublishAllMessages().Locally();
        });

        _host.ResetResourceState().GetAwaiter().GetResult();

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

}