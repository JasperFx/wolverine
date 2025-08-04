using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.SqlServer;

namespace EfCoreTests.MultiTenancy;

public class multi_tenancy_with_master_table_approach_sqlserver : MultiTenancyCompliance
{
    public multi_tenancy_with_master_table_approach_sqlserver() : base(DatabaseEngine.SqlServer)
    {
    }

    public override void Configure(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "dynamic_multi_tenancy")
            .UseMasterTableTenancy(tenants => 
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });
        
        // Little weird, but we have to remove this DbContext to use
        // the lightweight saga persistence
        opts.Services.RemoveAll(typeof(OrdersDbContext));
        opts.AddSagaType<Order>();
        
        opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
        {
            builder.UseSqlServer(connectionString.Value,
                b => b.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
        }, AutoCreate.CreateOrUpdate);

        opts.Services.AddResourceSetupOnStartup();
    }
}