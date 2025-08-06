using JasperFx;
using JasperFx.Core;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Scheduling;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.SqlServer;

namespace EfCoreTests.MultiTenancy;

public class MultiTenancyDocumentationSamples
{
    public async Task static_postgresql()
    {
        #region sample_static_tenant_registry_with_postgresql

        var builder = Host.CreateApplicationBuilder();
        
        var configuration = builder.Configuration;

        builder.UseWolverine(opts =>
        {
            // First, you do have to have a "main" PostgreSQL database for messaging persistence
            // that will store information about running nodes, agents, and non-tenanted operations
            opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("main"))

                // Add known tenants at bootstrapping time
                .RegisterStaticTenants(tenants =>
                {
                    // Add connection strings for the expected tenant ids
                    tenants.Register("tenant1", configuration.GetConnectionString("tenant1"));
                    tenants.Register("tenant2", configuration.GetConnectionString("tenant2"));
                    tenants.Register("tenant3", configuration.GetConnectionString("tenant3"));
                });
            
            opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
            {
                builder.UseNpgsql(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"));
            }, AutoCreate.CreateOrUpdate);
        });

        #endregion
    }
    
    public async Task static_sqlserver()
    {
        #region sample_static_tenant_registry_with_sqlserver

        var builder = Host.CreateApplicationBuilder();
        
        var configuration = builder.Configuration;

        builder.UseWolverine(opts =>
        {
            // First, you do have to have a "main" PostgreSQL database for messaging persistence
            // that will store information about running nodes, agents, and non-tenanted operations
            opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("main"))

                // Add known tenants at bootstrapping time
                .RegisterStaticTenants(tenants =>
                {
                    // Add connection strings for the expected tenant ids
                    tenants.Register("tenant1", configuration.GetConnectionString("tenant1"));
                    tenants.Register("tenant2", configuration.GetConnectionString("tenant2"));
                    tenants.Register("tenant3", configuration.GetConnectionString("tenant3"));
                });
            
            // Just to show that you *can* use more than one DbContext
            opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
            {
                // You might have to set the migration assembly
                builder.UseSqlServer(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
            }, AutoCreate.CreateOrUpdate);
        
            opts.Services.AddDbContextWithWolverineManagedMultiTenancy<OrdersDbContext>((builder, connectionString, _) =>
            {
                builder.UseSqlServer(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithSqlServer"));
            }, AutoCreate.CreateOrUpdate);
        });

        #endregion
    }

    public void dynamic_multi_tenancy_with_postgresql()
    {
        #region sample_using_postgresql_backed_master_table_tenancy

        var builder = Host.CreateApplicationBuilder();

        var configuration = builder.Configuration;
        builder.UseWolverine(opts =>
        {
            // You need a main database no matter what that will hold information about the Wolverine system itself
            // and..
            opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("wolverine"))

                // ...also a table holding the tenant id to connection string information
                .UseMasterTableTenancy(seed =>
                {
                    // These registrations are 100% just to seed data for local development
                    // Maybe you want to omit this during production?
                    // Or do something programmatic by looping through data in the IConfiguration?
                    seed.Register("tenant1", configuration.GetConnectionString("tenant1"));
                    seed.Register("tenant2", configuration.GetConnectionString("tenant2"));
                    seed.Register("tenant3", configuration.GetConnectionString("tenant3"));
                });

        });

        #endregion
    }
    
    public void dynamic_multi_tenancy_with_sqlserver()
    {
        #region sample_using_sqlserver_backed_master_table_tenancy

        var builder = Host.CreateApplicationBuilder();

        var configuration = builder.Configuration;
        builder.UseWolverine(opts =>
        {
            // You need a main database no matter what that will hold information about the Wolverine system itself
            // and..
            opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("wolverine"))

                // ...also a table holding the tenant id to connection string information
                .UseMasterTableTenancy(seed =>
                {
                    // These registrations are 100% just to seed data for local development
                    // Maybe you want to omit this during production?
                    // Or do something programmatic by looping through data in the IConfiguration?
                    seed.Register("tenant1", configuration.GetConnectionString("tenant1"));
                    seed.Register("tenant2", configuration.GetConnectionString("tenant2"));
                    seed.Register("tenant3", configuration.GetConnectionString("tenant3"));
                });
        });

        #endregion
    }

    public async Task static_postgresql_with_npgsql_data_source()
    {
        #region sample_adding_our_fancy_postgresql_multi_tenancy

        var host = Host.CreateDefaultBuilder()
            .UseWolverine()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IWolverineExtension, OurFancyPostgreSQLMultiTenancy>();
            }).StartAsync();

        #endregion
    }
}

#region sample_OurFancyPostgreSQLMultiTenancy

public class OurFancyPostgreSQLMultiTenancy : IWolverineExtension
{
    private readonly IServiceProvider _provider;

    public OurFancyPostgreSQLMultiTenancy(IServiceProvider provider)
    {
        _provider = provider;
    }

    public void Configure(WolverineOptions options)
    {
        options.PersistMessagesWithPostgresql(_provider.GetRequiredService<NpgsqlDataSource>())
            .RegisterStaticTenantsByDataSource(tenants =>
            {
                tenants.Register("tenant1", _provider.GetRequiredKeyedService<NpgsqlDataSource>("tenant1"));
                tenants.Register("tenant1", _provider.GetRequiredKeyedService<NpgsqlDataSource>("tenant2"));
                tenants.Register("tenant1", _provider.GetRequiredKeyedService<NpgsqlDataSource>("tenant3"));
            });
    }
}

#endregion

#region sample_using_IDbContextOutboxFactory

public class MyMessageHandler
{
    private readonly IDbContextOutboxFactory _factory;

    public MyMessageHandler(IDbContextOutboxFactory factory)
    {
        _factory = factory;
    }

    public async Task HandleAsync(CreateItem command, TenantId tenantId, CancellationToken cancellationToken)
    {
        // Get an EF Core DbContext wrapped in a Wolverine IDbContextOutbox<ItemsDbContext>
        // for message sending wrapped in a transaction spanning the DbContext and Wolverine
        var outbox = await _factory.CreateForTenantAsync<ItemsDbContext>(tenantId.Value, cancellationToken);
        var item = new Item { Name = command.Name, Id = CombGuidIdGeneration.NewGuid() };

        outbox.DbContext.Items.Add(item);
        
        // Don't worry, this messages doesn't *actually* get sent until
        // the transaction succeeds
        await outbox.PublishAsync(new ItemCreated { Id = item.Id });

        // Save and commit the unit of work with the outgoing message,
        // then "flush" the outgoing messages through Wolverine
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }
}

#endregion

