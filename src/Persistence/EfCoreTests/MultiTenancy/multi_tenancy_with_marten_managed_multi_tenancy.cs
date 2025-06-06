using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Marten;
using Wolverine.RDBMS;

namespace EfCoreTests.MultiTenancy;

public class multi_tenancy_with_marten_managed_multi_tenancy : MultiTenancyCompliance
{
    public multi_tenancy_with_marten_managed_multi_tenancy() : base(DatabaseEngine.PostgreSQL)
    {
    }

    public override void Configure(WolverineOptions opts)
    {
        #region sample_use_multi_tenancy_with_both_marten_and_ef_core

        opts.Services.AddMarten(m =>
        {
            m.MultiTenantedDatabases(x =>
            {
                x.AddSingleTenantDatabase(tenant1ConnectionString, "red");
                x.AddSingleTenantDatabase(tenant2ConnectionString, "blue");
                x.AddSingleTenantDatabase(tenant3ConnectionString, "green");
            });
        }).IntegrateWithWolverine(x =>
        {
            x.MasterDatabaseConnectionString = Servers.PostgresConnectionString;
        });

        opts.Services.AddDbContextWithWolverineManagedMultiTenancyByDbDataSource<ItemsDbContext>((builder, dataSource, _) =>
        {
            builder.UseNpgsql(dataSource, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
        }, AutoCreate.CreateOrUpdate);

        #endregion
        
        // Little weird, but we have to remove this DbContext to use
        // the lightweight saga persistence
        opts.Services.RemoveAll(typeof(OrdersDbContext));
        opts.AddSagaType<Order>();

        opts.Services.AddResourceSetupOnStartup();
    }
}