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

    public async ValueTask InitializeAsync()
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
                        // Bucketing is opt-in; enabling it here does not change the behavior of the
                        // non-bucketed tests, which still register tenants with no suffix
                        tenancy => tenancy.PartitionPerTenant(p => p.AllowPartitionSharing = true));
                }
                else
                {
                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "conjoined_part_wolverine");
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<PartitionedItemsDbContext>(
                        (builder, connectionString) => builder.UseSqlServer(connectionString.Value),
                        AutoCreate.CreateOrUpdate,
                        tenancy => tenancy.PartitionPerTenant(p => p.AllowPartitionSharing = true));
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

    public async ValueTask DisposeAsync()
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
        await thePartitions.AddTenantAsync("green", TestContext.Current.CancellationToken);
        await thePartitions.AddTenantAsync("blue", TestContext.Current.CancellationToken);

        var greenId = Guid.NewGuid();
        var blueId = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreatePartitionedItem(greenId, "g")));
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("blue", new CreatePartitionedItem(blueId, "b")));

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        (await green.Items.ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Id.ShouldBe(greenId);

        var blue = await theBuilder.BuildAsync("blue", CancellationToken.None);
        (await blue.Items.ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Id.ShouldBe(blueId);
    }

    [Fact]
    public async Task adding_the_same_tenant_twice_is_idempotent()
    {
        await thePartitions.AddTenantAsync("green", TestContext.Current.CancellationToken);
        await thePartitions.AddTenantAsync("green", TestContext.Current.CancellationToken);
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
    public async Task add_tenants_reports_the_outcome_for_every_managed_table()
    {
        var result = await thePartitions.AddTenantsAsync(new Dictionary<string, string?>
        {
            ["green"] = null,
            ["blue"] = null
        }, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        result.Failures.ShouldBeEmpty();

        // The partitioned entity table is reported; the saga table is excluded from
        // physical partitioning, so it is not part of the managed set
        result.Tables.ShouldContain(x => x.TableName.Contains("partitioned_items"));
        result.Tables.ShouldNotContain(x => x.TableName.Contains("partitioned_counters"));

        if (_engine == DatabaseEngine.SqlServer)
        {
            // SQL Server partitions over a compact ordinal, so the assigned map comes back
            result.Ordinals.Keys.OrderBy(x => x).ShouldBe(["blue", "green"]);
            result.Ordinals["green"].ShouldNotBe(result.Ordinals["blue"]);
        }
        else
        {
            // PostgreSQL partitions by list on the tenant id itself -- no ordinals
            result.Ordinals.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task back_fill_reconciles_every_managed_table_and_is_idempotent()
    {
        await thePartitions.AddTenantAsync("green", TestContext.Current.CancellationToken);
        await thePartitions.AddTenantAsync("blue", TestContext.Current.CancellationToken);

        var first = await thePartitions.MigrateTenantPartitionsAsync(TestContext.Current.CancellationToken);
        first.Succeeded.ShouldBeTrue();
        first.Tables.ShouldContain(x => x.TableName.Contains("partitioned_items"));

        // Back-fill is a reconcile, not a one-shot -- re-running it changes nothing
        var second = await thePartitions.MigrateTenantPartitionsAsync(TestContext.Current.CancellationToken);
        second.Succeeded.ShouldBeTrue();

        // and the tenants registered before the back-fill still write and read
        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("green", new CreatePartitionedItem(id, "g")));

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        (await green.Items.ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Id.ShouldBe(id);
    }

    [Fact]
    public async Task bucketed_tenants_registered_separately_share_one_partition()
    {
        // GH-3683 / weasel#391: registering bucket members ONE AT A TIME is the documented shape and the
        // natural tenant-onboarding shape, and it silently did not work on either engine. PostgreSQL's
        // second member was swallowed by CREATE TABLE IF NOT EXISTS so its first write failed with 23514;
        // SQL Server's registry had no bucket key, so each call quietly allocated a separate ordinal and
        // the tenants never actually shared the partition that bucketing exists to give them.
        await thePartitions.AddTenantAsync("smalla", "shared_bucket", TestContext.Current.CancellationToken);
        await thePartitions.AddTenantAsync("smallb", "shared_bucket", TestContext.Current.CancellationToken);

        // Both members read and write...
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("smalla", new CreatePartitionedItem(aId, "a")));
        await theHost.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("smallb", new CreatePartitionedItem(bId, "b")));

        var a = await theBuilder.BuildAsync("smalla", CancellationToken.None);
        (await a.Items.ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Id.ShouldBe(aId);

        var b = await theBuilder.BuildAsync("smallb", CancellationToken.None);
        (await b.Items.ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Id.ShouldBe(bId);

        // ...and they genuinely share ONE physical partition, which is the entire point
        (await distinctPartitionCountAsync(["smalla", "smallb"])).ShouldBe(1);
    }

    [Fact]
    public async Task bucketed_tenants_registered_together_share_one_partition()
    {
        await thePartitions.AddTenantsAsync(new Dictionary<string, string?>
        {
            ["smalla"] = "shared_bucket",
            ["smallb"] = "shared_bucket"
        }, TestContext.Current.CancellationToken);

        (await distinctPartitionCountAsync(["smalla", "smallb"])).ShouldBe(1);
    }

    [Fact]
    public async Task dropping_one_bucket_member_leaves_the_others_working()
    {
        // The co-tenant data-loss defect found alongside GH-3683: on PostgreSQL the by-value drop resolved
        // the tenant to its suffix and dropped BY SUFFIX, taking every co-tenant's rows with it.
        await thePartitions.AddTenantAsync("smalla", "shared_bucket", TestContext.Current.CancellationToken);
        await thePartitions.AddTenantAsync("smallb", "shared_bucket", TestContext.Current.CancellationToken);

        var survivorId = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("smalla", new CreatePartitionedItem(Guid.NewGuid(), "doomed")));
        await theHost.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("smallb", new CreatePartitionedItem(survivorId, "survivor")));

        await thePartitions.DropTenantAsync("smalla", deleteData: true, cancellationToken: TestContext.Current.CancellationToken);

        // The survivor keeps its rows...
        var b = await theBuilder.BuildAsync("smallb", CancellationToken.None);
        (await b.Items.ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Id.ShouldBe(survivorId);

        // ...and can still write
        var moreId = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("smallb", new CreatePartitionedItem(moreId, "more")));

        b = await theBuilder.BuildAsync("smallb", CancellationToken.None);
        (await b.Items.ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Select(x => x.Id).OrderBy(x => x)
            .ShouldBe(new[] { survivorId, moreId }.OrderBy(x => x));
    }

    /// <summary>
    ///     How many distinct physical partitions the given tenants occupy. PostgreSQL partitions by list on
    ///     the tenant id, so the partition is the child table the row lands in; SQL Server partitions over a
    ///     compact ordinal, so it is the registered ordinal
    /// </summary>
    private async Task<int> distinctPartitionCountAsync(string[] tenantIds)
    {
        if (_engine == DatabaseEngine.PostgreSQL)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
select count(distinct c.relname)
from pg_class c
join pg_inherits i on i.inhrelid = c.oid
join pg_class parent on i.inhparent = parent.oid
join pg_namespace ns on parent.relnamespace = ns.oid
where ns.nspname = 'conjoined_part' and parent.relname = 'partitioned_items'
  and pg_get_expr(c.relpartbound, c.oid) like any (@patterns)";
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "patterns";
            parameter.Value = tenantIds.Select(x => $"%'{x}'%").ToArray();
            cmd.Parameters.Add(parameter);
            return (int)(long)(await cmd.ExecuteScalarAsync())!;
        }

        await using var sqlConn = new SqlConnection(Servers.SqlServerConnectionString);
        await sqlConn.OpenAsync();
        await using var sqlCmd = sqlConn.CreateCommand();
        var names = tenantIds.Select((_, i) => $"@t{i}").ToArray();
        for (var i = 0; i < tenantIds.Length; i++)
        {
            sqlCmd.Parameters.AddWithValue($"@t{i}", tenantIds[i]);
        }

        sqlCmd.CommandText =
            $"SELECT COUNT(DISTINCT ordinal) FROM conjoined_part_wolverine.wolverine_tenant_partitions WHERE tenant_id IN ({string.Join(", ", names)})";
        return (int)(await sqlCmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task physical_partition_exists_per_tenant()
    {
        await thePartitions.AddTenantAsync("green", TestContext.Current.CancellationToken);

        if (_engine == DatabaseEngine.PostgreSQL)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
select count(*) from pg_inherits
join pg_class parent on pg_inherits.inhparent = parent.oid
join pg_namespace ns on parent.relnamespace = ns.oid
where ns.nspname = 'conjoined_part' and parent.relname = 'partitioned_items'";
            var partitionCount = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            partitionCount.ShouldBeGreaterThanOrEqualTo(1);
        }
        else
        {
            await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM conjoined_part_wolverine.wolverine_tenant_partitions WHERE tenant_id = 'green'";
            ((int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(1);
        }
    }
}

public class conjoined_partitioning_with_postgresql : ConjoinedPartitioningCompliance
{
    public conjoined_partitioning_with_postgresql() : base(DatabaseEngine.PostgreSQL)
    {
    }

    /// <summary>
    ///     The back-fill's reason to exist, in the shape it can actually be forced on
    ///     PostgreSQL: a partitioned table that is missing a partition for an already
    ///     registered tenant. A table that joins the managed set late is the same
    ///     situation -- routine migration deltas leave managed partitions alone, so
    ///     nothing else recreates it
    /// </summary>
    [Fact]
    public async Task back_fill_recreates_a_partition_missing_for_a_registered_tenant()
    {
        await thePartitions.AddTenantAsync("green", TestContext.Current.CancellationToken);

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var drop = conn.CreateCommand();
            drop.CommandText = "DROP TABLE conjoined_part.partitioned_items_green;";
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Without its partition, the tenant's writes have nowhere to land
        await Should.ThrowAsync<Exception>(async () =>
        {
            await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
                .ExecuteAndWaitAsync(c =>
                    c.InvokeForTenantAsync("green", new CreatePartitionedItem(Guid.NewGuid(), "before")));
        });

        var result = await thePartitions.MigrateTenantPartitionsAsync(TestContext.Current.CancellationToken);
        result.Succeeded.ShouldBeTrue();

        var id = Guid.NewGuid();
        await theHost.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("green", new CreatePartitionedItem(id, "after")));

        var green = await theBuilder.BuildAsync("green", CancellationToken.None);
        (await green.Items.ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Id.ShouldBe(id);
    }
}

public class conjoined_partitioning_with_sqlserver : ConjoinedPartitioningCompliance
{
    public conjoined_partitioning_with_sqlserver() : base(DatabaseEngine.SqlServer)
    {
    }
}
