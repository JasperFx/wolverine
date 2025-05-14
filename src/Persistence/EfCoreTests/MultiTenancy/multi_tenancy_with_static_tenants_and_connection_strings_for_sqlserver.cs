using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy;

public class multi_tenancy_with_static_tenants_and_connection_strings_for_sqlserver : MultiTenancyCompliance
{
    public multi_tenancy_with_static_tenants_and_connection_strings_for_sqlserver() : base(DatabaseEngine.SqlServer)
    {
    }

    public override void Configure(WolverineOptions opts)
    {
        
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "static_multi_tenancy")
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
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
    public async Task opens_the_db_context_to_the_correct_database_1()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "blue";

        var blue = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        var builder = new SqlConnectionStringBuilder(blue.Database.GetConnectionString());
        builder.InitialCatalog.ShouldBe("db2");
    }

    [Fact]
    public async Task opens_the_db_context_to_the_correct_database_2()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "red";

        var blue = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        var builder = new SqlConnectionStringBuilder(blue.Database.GetConnectionString());
        builder.InitialCatalog.ShouldBe("db1");
    }

    [Fact]
    public async Task opens_the_db_context_to_the_correct_database_3()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "green";

        var blue = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        var builder = new SqlConnectionStringBuilder(blue.Database.GetConnectionString());
        builder.InitialCatalog.ShouldBe("db3");
    }


}