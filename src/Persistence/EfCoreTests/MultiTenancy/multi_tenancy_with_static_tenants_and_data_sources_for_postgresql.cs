using System.Data.Common;
using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy;

public class multi_tenancy_with_static_tenants_and_data_sources_for_postgresql : MultiTenancyCompliance
{
    

    public multi_tenancy_with_static_tenants_and_data_sources_for_postgresql() : base(DatabaseEngine.PostgreSQL)
    {
    }

    public override void Configure(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(NpgsqlDataSource.Create(Servers.PostgresConnectionString), "static_multi_tenancy")
            .RegisterStaticTenantsByDataSource(tenants =>
            {
                tenants.Register("red", NpgsqlDataSource.Create(tenant1ConnectionString));
                tenants.Register("blue", NpgsqlDataSource.Create(tenant2ConnectionString));
                tenants.Register("green", NpgsqlDataSource.Create(tenant3ConnectionString));
            });
        
        // Little weird, but we have to remove this DbContext to use
        // the lightweight saga persistence
        opts.Services.RemoveAll(typeof(OrdersDbContext));
        opts.AddSagaType<Order>();
        
        opts.Services.AddDbContextWithWolverineManagedMultiTenancyByDbDataSource<ItemsDbContext>((builder, dataSource, _) =>
        {
            builder.UseNpgsql<ItemsDbContext>((DbDataSource)dataSource, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
        }, AutoCreate.CreateOrUpdate);

        opts.Services.AddResourceSetupOnStartup();
    }
    
    [Fact]
    public async Task opens_the_db_context_to_the_correct_database_1()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "blue";

        var blue = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        var builder = new NpgsqlConnectionStringBuilder(blue.Database.GetConnectionString());
        builder.Database.ShouldBe("db2");
    }

    [Fact]
    public async Task opens_the_db_context_to_the_correct_database_2()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "red";

        var blue = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        var builder = new NpgsqlConnectionStringBuilder(blue.Database.GetConnectionString());
        builder.Database.ShouldBe("db1");
    }

    [Fact]
    public async Task opens_the_db_context_to_the_correct_database_3()
    {
        var messageContext = new MessageContext(theHost.GetRuntime());
        messageContext.TenantId = "green";

        var blue = await theBuilder.BuildAndEnrollAsync(messageContext, CancellationToken.None);
        var builder = new NpgsqlConnectionStringBuilder(blue.Database.GetConnectionString());
        builder.Database.ShouldBe("db3");
    }
}