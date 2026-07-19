using IntegrationTests;
using JasperFx;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Postgresql;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using ConjoinedTenancyRuntime = Wolverine.EntityFrameworkCore.Internals.ConjoinedTenancy;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

public class PartitionedItem : ITenanted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? TenantId { get; set; }
}

// Sagas are excluded from physical partitioning in this release; this type
// proves the exclusion leaves the saga's identity alone
[WolverineIgnore]
public class PartitionedCounterSaga : Saga, ITenanted
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public string? TenantId { get; set; }

    public static PartitionedCounterSaga Start(StartCounter command)
    {
        return new PartitionedCounterSaga { Id = command.Id };
    }

    public void Handle(IncrementCounter command)
    {
        Count++;
    }
}

public record CreatePartitionedItem(Guid Id, string Name);

[WolverineIgnore]
public class PartitionedItemHandler
{
    public static void Handle(CreatePartitionedItem command, PartitionedItemsDbContext db)
    {
        db.Items.Add(new PartitionedItem { Id = command.Id, Name = command.Name });
    }
}

public class PartitionedItemsDbContext : DbContext
{
    public PartitionedItemsDbContext(DbContextOptions<PartitionedItemsDbContext> options) : base(options)
    {
    }

    public DbSet<PartitionedItem> Items { get; set; } = null!;
    public DbSet<PartitionedCounterSaga> Counters { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PartitionedItem>(map =>
        {
            map.ToTable("partitioned_items", "conjoined_part");
            map.HasKey(x => x.Id);
        });

        modelBuilder.Entity<PartitionedCounterSaga>(map =>
        {
            map.ToTable("partitioned_counters", "conjoined_part");
            map.HasKey(x => x.Id);
        });
    }
}

[Collection("multi-tenancy")]
public abstract class ConjoinedPartitioningCompliance : IAsyncLifetime
{
    private readonly DatabaseEngine _engine;
    protected IConjoinedTenantPartitions<PartitionedItemsDbContext> thePartitions = null!;
    protected IDbContextBuilder<PartitionedItemsDbContext> theBuilder = null!;
    protected IHost theHost = null!;

    protected ConjoinedPartitioningCompliance(DatabaseEngine engine)
    {
        _engine = engine;
    }

    public async Task InitializeAsync()
    {
        await dropPartitionedObjectsAsync();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<PartitionedItemHandler>()
                    .IncludeType<PartitionedCounterSaga>();

                if (_engine == DatabaseEngine.PostgreSQL)
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_part_wolverine");
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<PartitionedItemsDbContext>(
                        (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                        AutoCreate.CreateOrUpdate,
                        tenancy => tenancy.PartitionPerTenant());
                }
                else
                {
                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "conjoined_part_wolverine");
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<PartitionedItemsDbContext>(
                        (builder, connectionString) => builder.UseSqlServer(connectionString.Value),
                        AutoCreate.CreateOrUpdate,
                        tenancy => tenancy.PartitionPerTenant());
                }

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();
                opts.PublishAllMessages().Locally();
            }).StartAsync();

        theBuilder = theHost.Services.GetRequiredService<IDbContextBuilder<PartitionedItemsDbContext>>();
        thePartitions = theHost.Services
            .GetRequiredService<IConjoinedTenantPartitions<PartitionedItemsDbContext>>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task dropPartitionedObjectsAsync()
    {
        if (_engine == DatabaseEngine.PostgreSQL)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS conjoined_part CASCADE; DROP SCHEMA IF EXISTS conjoined_part_wolverine CASCADE;";
            await cmd.ExecuteNonQueryAsync();
        }
        else
        {
            await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
IF OBJECT_ID('conjoined_part.partitioned_items') IS NOT NULL DROP TABLE conjoined_part.partitioned_items;
IF OBJECT_ID('conjoined_part.partitioned_counters') IS NOT NULL DROP TABLE conjoined_part.partitioned_counters;
IF OBJECT_ID('conjoined_part_wolverine.wolverine_tenant_partitions') IS NOT NULL DROP TABLE conjoined_part_wolverine.wolverine_tenant_partitions;
IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'ps_partitioned_items_tenant_ordinal') DROP PARTITION SCHEME ps_partitioned_items_tenant_ordinal;
IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = 'pf_partitioned_items_tenant_ordinal') DROP PARTITION FUNCTION pf_partitioned_items_tenant_ordinal;";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ef_model_keys_stay_single_and_sqlserver_maps_the_ordinal_column()
    {
        var context = await theBuilder.BuildAsync(CancellationToken.None);

        // The composite (tenant, id) key lives only in the DATABASE; the EF model
        // keeps the user's own key so FindAsync/Attach and saga loads are unchanged
        var itemType = context.Model.FindEntityType(typeof(PartitionedItem))!;
        itemType.FindPrimaryKey()!.Properties.Select(x => x.Name).ShouldBe([nameof(PartitionedItem.Id)]);

        if (_engine == DatabaseEngine.SqlServer)
        {
            var ordinal = itemType.FindProperty(ConjoinedTenancyRuntime.TenantOrdinalPropertyName);
            ordinal.ShouldNotBeNull();
            ordinal.IsPrimaryKey().ShouldBeFalse();
        }

        // Saga identity is untouched by partitioning
        var sagaType = context.Model.FindEntityType(typeof(PartitionedCounterSaga))!;
        sagaType.FindPrimaryKey()!.Properties.Select(x => x.Name)
            .ShouldBe([nameof(PartitionedCounterSaga.Id)]);
    }

    [Fact]
    public async Task add_tenants_then_write_and_read_per_tenant()
    {
        await thePartitions.AddTenantAsync("green");
        await thePartitions.AddTenantAsync("blue");

        var greenId = Guid.NewGuid();
        var blueId = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreatePartitionedItem(greenId, "g")));
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("blue", new CreatePartitionedItem(blueId, "b")));

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        (await green.Items.ToListAsync()).Single().Id.ShouldBe(greenId);

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        (await blue.Items.ToListAsync()).Single().Id.ShouldBe(blueId);
    }

    [Fact]
    public async Task adding_the_same_tenant_twice_is_idempotent()
    {
        await thePartitions.AddTenantAsync("green");
        await thePartitions.AddTenantAsync("green");
    }

    [Fact]
    public async Task writes_for_an_unregistered_tenant_fail()
    {
        var id = Guid.NewGuid();

        await Should.ThrowAsync<Exception>(async () =>
        {
            await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
                .ExecuteAndWaitAsync(c =>
                    c.InvokeForTenantAsync("nobody-registered-this", new CreatePartitionedItem(id, "x")));
        });
    }

    [Fact]
    public async Task physical_partition_exists_per_tenant()
    {
        await thePartitions.AddTenantAsync("green");

        if (_engine == DatabaseEngine.PostgreSQL)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
select count(*) from pg_inherits
join pg_class parent on pg_inherits.inhparent = parent.oid
join pg_namespace ns on parent.relnamespace = ns.oid
where ns.nspname = 'conjoined_part' and parent.relname = 'partitioned_items'";
            var partitionCount = (long)(await cmd.ExecuteScalarAsync())!;
            partitionCount.ShouldBeGreaterThanOrEqualTo(1);
        }
        else
        {
            await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM conjoined_part_wolverine.wolverine_tenant_partitions WHERE tenant_id = 'green'";
            ((int)(await cmd.ExecuteScalarAsync())!).ShouldBe(1);
        }
    }
}

public class conjoined_partitioning_with_postgresql : ConjoinedPartitioningCompliance
{
    public conjoined_partitioning_with_postgresql() : base(DatabaseEngine.PostgreSQL)
    {
    }
}

public class conjoined_partitioning_with_sqlserver : ConjoinedPartitioningCompliance
{
    public conjoined_partitioning_with_sqlserver() : base(DatabaseEngine.SqlServer)
    {
    }
}
