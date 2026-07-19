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
using Wolverine.EntityFrameworkCore.Internals;
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
            opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("main")!)

                // Add known tenants at bootstrapping time
                .RegisterStaticTenants(tenants =>
                {
                    // Add connection strings for the expected tenant ids
                    tenants.Register("tenant1", configuration.GetConnectionString("tenant1")!);
                    tenants.Register("tenant2", configuration.GetConnectionString("tenant2")!);
                    tenants.Register("tenant3", configuration.GetConnectionString("tenant3")!);
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
            opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("main")!)

                // Add known tenants at bootstrapping time
                .RegisterStaticTenants(tenants =>
                {
                    // Add connection strings for the expected tenant ids
                    tenants.Register("tenant1", configuration.GetConnectionString("tenant1")!);
                    tenants.Register("tenant2", configuration.GetConnectionString("tenant2")!);
                    tenants.Register("tenant3", configuration.GetConnectionString("tenant3")!);
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
            opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("wolverine")!)

                // ...also a table holding the tenant id to connection string information
                .UseMasterTableTenancy(seed =>
                {
                    // These registrations are 100% just to seed data for local development
                    // Maybe you want to omit this during production?
                    // Or do something programmatic by looping through data in the IConfiguration?
                    seed.Register("tenant1", configuration.GetConnectionString("tenant1")!);
                    seed.Register("tenant2", configuration.GetConnectionString("tenant2")!);
                    seed.Register("tenant3", configuration.GetConnectionString("tenant3")!);
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
            opts.PersistMessagesWithSqlServer(configuration.GetConnectionString("wolverine")!)

                // ...also a table holding the tenant id to connection string information
                .UseMasterTableTenancy(seed =>
                {
                    // These registrations are 100% just to seed data for local development
                    // Maybe you want to omit this during production?
                    // Or do something programmatic by looping through data in the IConfiguration?
                    seed.Register("tenant1", configuration.GetConnectionString("tenant1")!);
                    seed.Register("tenant2", configuration.GetConnectionString("tenant2")!);
                    seed.Register("tenant3", configuration.GetConnectionString("tenant3")!);
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

#region sample_ourfancypostgresqlmultitenancy
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
                tenants.Register("tenant2", _provider.GetRequiredKeyedService<NpgsqlDataSource>("tenant2"));
                tenants.Register("tenant3", _provider.GetRequiredKeyedService<NpgsqlDataSource>("tenant3"));
            });
    }
}

#endregion

#region sample_using_idbcontextoutboxfactory
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

public record CreateItem(string Name);

public class ConjoinedTenancyDocumentationSamples
{
    public async Task conjoined_postgresql()
    {
        #region sample_conjoined_tenancy_with_postgresql
        var builder = Host.CreateApplicationBuilder();

        var configuration = builder.Configuration;

        builder.UseWolverine(opts =>
        {
            // One single database for messaging persistence *and*
            // all tenanted application data
            opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("main")!);

            // Conjoined multi-tenancy: every entity implementing
            // JasperFx.MultiTenancy.ITenanted is mapped with a tenant_id column,
            // filtered by the current tenant on every query, stamped with the
            // ambient tenant id on inserts, and guarded against cross-tenant
            // updates and deletes
            opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedTenancy.ConjoinedItemsDbContext>(
                (builder, connectionString) =>
                {
                    builder.UseNpgsql(connectionString.Value);
                }, AutoCreate.CreateOrUpdate);
        });

        #endregion
    }

    public async Task conjoined_partitioned_postgresql()
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = builder.Configuration;

        builder.UseWolverine(opts =>
        {
            opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("main")!);

            #region sample_conjoined_tenancy_with_partitioning
            opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedTenancy.ConjoinedItemsDbContext>(
                (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                AutoCreate.CreateOrUpdate,

                // Weasel-managed physical partitioning: one partition (or shared
                // bucket) per tenant on every non-saga ITenanted entity table
                tenancy => tenancy.PartitionPerTenant());
            #endregion
        });
    }

    public static async Task conjoined_tenant_management(IHost host)
    {
        #region sample_conjoined_partitioning_tenant_management
        var partitions = host.Services
            .GetRequiredService<IConjoinedTenantPartitions<ConjoinedTenancy.ConjoinedItemsDbContext>>();

        // Each tenant gets its own physical partition
        await partitions.AddTenantAsync("tenant1");

        // Or share one partition between small tenants ("bucketing") --
        // requires AllowPartitionSharing on the partitioning options
        await partitions.AddTenantAsync("small-tenant-a", "shared_bucket");
        await partitions.AddTenantAsync("small-tenant-b", "shared_bucket");

        // Dropping a tenant's partition removes its rows
        await partitions.DropTenantAsync("tenant1", deleteData: true);
        #endregion

        #region sample_conjoined_tenant_registry
        var tenants = host.Services.GetRequiredService<IDynamicTenantSource<string>>();

        // Registers the tenant in wolverine_tenants (and creates its
        // partitions when partitioning is enabled)
        await tenants.AddTenantAsync("tenant1", CancellationToken.None);

        // Soft delete: the tenant's data stays, but writes are rejected
        await tenants.DisableTenantAsync("tenant1");
        await tenants.EnableTenantAsync("tenant1");

        // Hard delete: registry record removed; with partitioning enabled the
        // tenant's partition is dropped along with its rows
        await tenants.RemoveTenantAsync("tenant1");
        #endregion
    }

    #region sample_conjoined_tenanted_entity

    // Implementing the JasperFx.MultiTenancy.ITenanted interface --
    // the same marker interface Marten uses for conjoined tenancy --
    // opts this entity into Wolverine's conjoined multi-tenancy
    public class TenantedItem : ITenanted
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = null!;

        // Wolverine maps, stamps, and hydrates this for you. Treat the
        // value as framework-managed
        public string? TenantId { get; set; }
    }

    #endregion
}

