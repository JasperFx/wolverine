using EfCoreTests.MultiTenancy;
using IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using SharedPersistenceModels.Items;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace EfCoreTests;

public class Bug_661_postgresql_with_ef_core
{
#if NET8_0_OR_GREATER
    [Fact]
    public async Task can_set_up_with_default_schema_name()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(o =>
                {
                    o.UseNpgsql(Servers.PostgresConnectionString);
                });
                
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
                opts.Services.AddResourceSetupOnStartup();
                opts.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(Servers.PostgresConnectionString));
            }).StartAsync();
    }
#endif
}