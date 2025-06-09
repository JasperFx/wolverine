using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using SharedPersistenceModels;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Wolverine;
using Wolverine.Http;

namespace MultiTenantedEfCoreWithPostgreSQL;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddDbContext<ItemsDbContext>(x =>
        {
            x.UseNpgsql(Servers.PostgresConnectionString, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
        });
        builder.Services.AddDbContext<OrdersDbContext>(x => x.UseNpgsql(Servers.PostgresConnectionString, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL")));

        builder.Host.UseWolverine(opts =>
        {
            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();
    
            TestingOverrides.Extension?.Configure(opts);
        });

        builder.Services.AddWolverineHttp();
        builder.Services.AddResourceSetupOnStartup();

        var app = builder.Build();

        app.MapWolverineEndpoints(opts =>
        {
            // Set up tenant detection
            opts.TenantId.IsQueryStringValue("tenant");
            opts.TenantId.DefaultIs(StorageConstants.DefaultTenantId);
        });

        return await app.RunJasperFxCommands(args);
    }
}


