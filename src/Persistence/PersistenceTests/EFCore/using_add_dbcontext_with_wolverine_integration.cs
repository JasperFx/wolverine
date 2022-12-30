using System.Threading.Tasks;
using IntegrationTests;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using Xunit;

namespace PersistenceTests.EFCore;

[Collection("sqlserver")]
public class using_add_dbcontext_with_wolverine_integration : IAsyncLifetime
{
    private IHost _host;
    
        public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                opts.UseEntityFrameworkCoreTransactions();
            }).StartAsync();


    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
    
    [Fact]
    public void is_wolverine_enabled()
    {
        using var nested = _host.Services.As<IContainer>().GetNestedContainer();
        nested.GetInstance<CleanDbContext>().IsWolverineEnabled().ShouldBeTrue();
    }


    
}