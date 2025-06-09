using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy;

public class multi_tenancy_with_shared_database_between_tenants_sql_server : MultiTenancyCompliance
{
    public multi_tenancy_with_shared_database_between_tenants_sql_server() : base(DatabaseEngine.SqlServer)
    {
    }

    public override void Configure(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.Solo;
        
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "static_multi_tenancy")
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
                tenants.Register("purple", tenant2ConnectionString);
                tenants.Register("orange", tenant3ConnectionString);
            });

        opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
        {
            builder.UseSqlServer(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
        }, AutoCreate.CreateOrUpdate);
        
        opts.Services.AddDbContextWithWolverineManagedMultiTenancy<OrdersDbContext>((builder, connectionString, _) =>
        {
            builder.UseSqlServer(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
        }, AutoCreate.CreateOrUpdate);

        opts.Services.AddResourceSetupOnStartup();

    }

    [Fact]
    public void expected_agents_do_not_duplicate_for_reused_database()
    {
        var runtime = theHost.GetRuntime();
        var agents = runtime.Agents.AllRunningAgentUris().Where(x => x.Scheme == "wolverinedb");
        agents.Count().ShouldBe(4);
    }
}