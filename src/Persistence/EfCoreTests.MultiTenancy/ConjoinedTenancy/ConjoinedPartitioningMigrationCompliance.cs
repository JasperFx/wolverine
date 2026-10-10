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
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

public class MigratingItem : ITenanted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? TenantId { get; set; }
}

public record CreateMigratingItem(Guid Id, string Name);

[WolverineIgnore]
public class MigratingItemHandler
{
    public static void Handle(CreateMigratingItem command, MigratingItemsDbContext db)
    {
        db.Items.Add(new MigratingItem { Id = command.Id, Name = command.Name });
    }
}

// Its own DbContext type rather than PartitionedItemsDbContext: the conjoined options are tracked per
// context TYPE, and these tests register the same type twice in a row -- once plain, once partitioned
public class MigratingItemsDbContext : DbContext
{
    public const string SchemaName = "conjoined_migr";

    public MigratingItemsDbContext(DbContextOptions<MigratingItemsDbContext> options) : base(options)
    {
    }

    public DbSet<MigratingItem> Items { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigratingItem>(map =>
        {
            map.ToTable("migrating_items", SchemaName);
            map.HasKey(x => x.Id);
        });
    }
}

/// <summary>
/// GH-3541. The migration story for switching PartitionPerTenant() on over a conjoined table that
/// already exists -- with data. Neither engine can convert a plain table to a partitioned one in
/// place, and before this the partition DDL simply failed against the plain table with a raw engine
/// error a long way from the cause.
///
/// <para>Every scenario starts the same way: a conjoined host WITHOUT partitioning creates the table
/// and writes rows for two tenants, then stops. What happens next is the subject.</para>
/// </summary>
[Collection("multi-tenancy")]
public abstract class ConjoinedPartitioningMigrationCompliance : IAsyncLifetime
{
    private const string WolverineSchema = "conjoined_migr_wolverine";
    private readonly DatabaseEngine _engine;

    protected static readonly Guid GreenOne = Guid.NewGuid();
    protected static readonly Guid GreenTwo = Guid.NewGuid();
    protected static readonly Guid BlueOne = Guid.NewGuid();

    protected ConjoinedPartitioningMigrationCompliance(DatabaseEngine engine)
    {
        _engine = engine;
    }

    public async ValueTask InitializeAsync()
    {
        await dropEverythingAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<IHost> startHostAsync(bool partitioned, bool migrateOnStartup)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<MigratingItemHandler>();

                Action<ConjoinedTenancyOptions>? tenancy = partitioned ? t => t.PartitionPerTenant() : null;

                if (_engine == DatabaseEngine.PostgreSQL)
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, WolverineSchema);
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<MigratingItemsDbContext>(
                        (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                        AutoCreate.CreateOrUpdate, tenancy);
                }
                else
                {
                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, WolverineSchema);
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<MigratingItemsDbContext>(
                        (builder, connectionString) => builder.UseSqlServer(connectionString.Value),
                        AutoCreate.CreateOrUpdate, tenancy);
                }

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                if (migrateOnStartup)
                {
                    opts.Services.AddResourceSetupOnStartup();
                }

                opts.PublishAllMessages().Locally();
            }).StartAsync();
    }

    /// <summary>
    /// The starting position: a plain conjoined table with two tenants' rows in it, and no host running
    /// </summary>
    private async Task seedPlainTableAsync(bool withRows = true)
    {
        using var host = await startHostAsync(partitioned: false, migrateOnStartup: true);

        if (withRows)
        {
            await host.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateMigratingItem(GreenOne, "g1")));
            await host.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateMigratingItem(GreenTwo, "g2")));
            await host.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("blue", new CreateMigratingItem(BlueOne, "b1")));
        }

        await host.StopAsync(TestContext.Current.CancellationToken);
        (await isPartitionedAsync()).ShouldBeFalse("the seed must leave a PLAIN table behind");
    }

    [Fact]
    public async Task startup_migration_refuses_loudly_when_a_plain_table_holds_rows()
    {
        await seedPlainTableAsync();

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await startHostAsync(partitioned: true, migrateOnStartup: true);
        });

        var loud = findInChain<UnpartitionedTenantTableException>(ex);
        loud.ShouldNotBeNull($"Expected UnpartitionedTenantTableException somewhere in the chain, got: {ex}");
        loud.DbContextType.ShouldBe(typeof(MigratingItemsDbContext));
        var table = loud.Tables.Single();
        table.Identifier.QualifiedName.ShouldBe($"{MigratingItemsDbContext.SchemaName}.migrating_items");
        table.RowCount.ShouldBe(3);
        table.TenantIds.OrderBy(x => x).ShouldBe(["blue", "green"]);

        // The message has to carry the way out, because this is where an operator first meets the problem
        loud.Message.ShouldContain("migrating_items");
        loud.Message.ShouldContain("RebuildUnpartitionedTablesAsync");
        loud.Message.ShouldContain("WriteRebuildScriptAsync");

        // ...and nothing was touched: the rows are all still there, in the plain table
        (await isPartitionedAsync()).ShouldBeFalse();
        (await rowCountAsync("migrating_items")).ShouldBe(3);
    }

    [Fact]
    public async Task registering_a_tenant_refuses_loudly_instead_of_failing_the_partition_ddl()
    {
        await seedPlainTableAsync();

        using var host = await startHostAsync(partitioned: true, migrateOnStartup: false);
        var partitions = host.Services.GetRequiredService<IConjoinedTenantPartitions<MigratingItemsDbContext>>();

        var ex = await Should.ThrowAsync<UnpartitionedTenantTableException>(() =>
            partitions.AddTenantAsync("red", TestContext.Current.CancellationToken));
        ex.Tables.Single().Identifier.Name.ShouldBe("migrating_items");

        // The back-fill is the other entry point that runs partition DDL, so it refuses the same way
        await Should.ThrowAsync<UnpartitionedTenantTableException>(() =>
            partitions.MigrateTenantPartitionsAsync(TestContext.Current.CancellationToken));

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task rebuild_turns_the_plain_table_into_a_partitioned_one_and_keeps_every_tenants_rows()
    {
        await seedPlainTableAsync();

        using var host = await startHostAsync(partitioned: true, migrateOnStartup: false);
        var partitions = host.Services.GetRequiredService<IConjoinedTenantPartitions<MigratingItemsDbContext>>();

        var found = await partitions.FindUnpartitionedTablesAsync(TestContext.Current.CancellationToken);
        found.Single().RowCount.ShouldBe(3);

        var rebuilt = await partitions.RebuildUnpartitionedTablesAsync(TestContext.Current.CancellationToken);
        rebuilt.ShouldBe([$"{MigratingItemsDbContext.SchemaName}.migrating_items"]);

        // Partitioned now, by the catalog's account rather than Wolverine's, and the copy is gone
        (await isPartitionedAsync()).ShouldBeTrue();
        (await tableExistsAsync("migrating_items_unpartitioned")).ShouldBeFalse();
        (await partitions.FindUnpartitionedTablesAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();

        // Both tenants were registered from their rows, and each still sees exactly its own
        var builder = host.Services.GetRequiredService<IDbContextBuilder<MigratingItemsDbContext>>();
        await using (var green = await builder.BuildAsync("green", CancellationToken.None))
        {
            (await green.Items.Select(x => x.Id).ToListAsync(TestContext.Current.CancellationToken))
                .OrderBy(x => x).ShouldBe(new[] { GreenOne, GreenTwo }.OrderBy(x => x));
        }

        await using (var blue = await builder.BuildAsync("blue", CancellationToken.None))
        {
            (await blue.Items.Select(x => x.Id).ToListAsync(TestContext.Current.CancellationToken)).ShouldBe([BlueOne]);
        }

        // The table is a working partitioned table from here on: new rows land, and new tenants register
        var greenThree = Guid.NewGuid();
        await host.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateMigratingItem(greenThree, "g3")));
        await using (var green = await builder.BuildAsync("green", CancellationToken.None))
        {
            (await green.Items.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(3);
        }

        (await partitions.AddTenantAsync("red", TestContext.Current.CancellationToken)).Succeeded.ShouldBeTrue();

        // Rebuilding again is a no-op, not a second rebuild
        (await partitions.RebuildUnpartitionedTablesAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_rebuild_script_can_be_reviewed_and_applied_by_hand()
    {
        await seedPlainTableAsync();

        using var host = await startHostAsync(partitioned: true, migrateOnStartup: false);
        var partitions = host.Services.GetRequiredService<IConjoinedTenantPartitions<MigratingItemsDbContext>>();

        var script = await partitions.WriteRebuildScriptAsync(TestContext.Current.CancellationToken);
        script.ShouldContain("migrating_items_unpartitioned");
        script.ShouldContain("migrating_items");

        // Writing the script registers the tenants it found, so the DDL's partitions match the data
        (await isPartitionedAsync()).ShouldBeFalse("writing the script must not apply it");

        await executeAsync(script);

        (await isPartitionedAsync()).ShouldBeTrue();
        (await rowCountAsync("migrating_items")).ShouldBe(3);
        (await tableExistsAsync("migrating_items_unpartitioned")).ShouldBeFalse();
        (await partitions.FindUnpartitionedTablesAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();

        var builder = host.Services.GetRequiredService<IDbContextBuilder<MigratingItemsDbContext>>();
        await using var blue = await builder.BuildAsync("blue", CancellationToken.None);
        (await blue.Items.Select(x => x.Id).ToListAsync(TestContext.Current.CancellationToken)).ShouldBe([BlueOne]);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_empty_plain_table_is_recreated_partitioned_by_the_startup_migration()
    {
        await seedPlainTableAsync(withRows: false);

        // No rows means nothing to protect: the startup migration recreates the table partitioned
        using var host = await startHostAsync(partitioned: true, migrateOnStartup: true);

        (await isPartitionedAsync()).ShouldBeTrue();

        var partitions = host.Services.GetRequiredService<IConjoinedTenantPartitions<MigratingItemsDbContext>>();
        (await partitions.AddTenantAsync("green", TestContext.Current.CancellationToken)).Succeeded.ShouldBeTrue();

        var id = Guid.NewGuid();
        await host.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("green", new CreateMigratingItem(id, "g")));

        var builder = host.Services.GetRequiredService<IDbContextBuilder<MigratingItemsDbContext>>();
        await using var green = await builder.BuildAsync("green", CancellationToken.None);
        (await green.Items.Select(x => x.Id).ToListAsync(TestContext.Current.CancellationToken)).ShouldBe([id]);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static T? findInChain<T>(Exception ex) where T : Exception
    {
        var queue = new Queue<Exception>([ex]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is T match) return match;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) queue.Enqueue(inner);
            }
            else if (current.InnerException != null)
            {
                queue.Enqueue(current.InnerException);
            }
        }

        return null;
    }

    // ---- catalog helpers: the assertions read the database, not Wolverine --------------------

    private async Task<bool> isPartitionedAsync()
    {
        if (_engine == DatabaseEngine.PostgreSQL)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "select c.relkind from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = :s and c.relname = 'migrating_items'";
            cmd.Parameters.AddWithValue("s", MigratingItemsDbContext.SchemaName);
            var kind = await cmd.ExecuteScalarAsync();
            kind.ShouldNotBeNull("the table has to exist for this question to mean anything");
            return (char)kind == 'p';
        }

        await using var sql = new SqlConnection(Servers.SqlServerConnectionString);
        await sql.OpenAsync();
        await using var probe = sql.CreateCommand();
        probe.CommandText = $@"
select case when exists (select 1 from sys.indexes i join sys.partition_schemes ps on ps.data_space_id = i.data_space_id
                         where i.object_id = object_id('{MigratingItemsDbContext.SchemaName}.migrating_items') and i.index_id in (0, 1)) then 1 else 0 end";
        (await tableExistsAsync("migrating_items")).ShouldBeTrue("the table has to exist for this question to mean anything");
        return (int)(await probe.ExecuteScalarAsync())! == 1;
    }

    private async Task<bool> tableExistsAsync(string table)
    {
        if (_engine == DatabaseEngine.PostgreSQL)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "select count(*) from pg_tables where schemaname = :s and tablename = :t";
            cmd.Parameters.AddWithValue("s", MigratingItemsDbContext.SchemaName);
            cmd.Parameters.AddWithValue("t", table);
            return (long)(await cmd.ExecuteScalarAsync())! == 1;
        }

        await using var sql = new SqlConnection(Servers.SqlServerConnectionString);
        await sql.OpenAsync();
        await using var probe = sql.CreateCommand();
        probe.CommandText = $"select case when object_id('{MigratingItemsDbContext.SchemaName}.{table}') is null then 0 else 1 end";
        return (int)(await probe.ExecuteScalarAsync())! == 1;
    }

    private async Task<long> rowCountAsync(string table)
    {
        if (_engine == DatabaseEngine.PostgreSQL)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"select count(*) from {MigratingItemsDbContext.SchemaName}.{table}";
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        await using var sql = new SqlConnection(Servers.SqlServerConnectionString);
        await sql.OpenAsync();
        await using var count = sql.CreateCommand();
        count.CommandText = $"select count_big(*) from {MigratingItemsDbContext.SchemaName}.{table}";
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private async Task executeAsync(string script)
    {
        if (_engine == DatabaseEngine.PostgreSQL)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = script;
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            return;
        }

        await using var sql = new SqlConnection(Servers.SqlServerConnectionString);
        await sql.OpenAsync();
        await using var sqlTx = (SqlTransaction)await sql.BeginTransactionAsync();
        await using var sqlCmd = sql.CreateCommand();
        sqlCmd.Transaction = sqlTx;
        sqlCmd.CommandText = script;
        await sqlCmd.ExecuteNonQueryAsync();
        await sqlTx.CommitAsync();
    }

    private async Task dropEverythingAsync()
    {
        if (_engine == DatabaseEngine.PostgreSQL)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"DROP SCHEMA IF EXISTS {MigratingItemsDbContext.SchemaName} CASCADE; DROP SCHEMA IF EXISTS {WolverineSchema} CASCADE;";
            await cmd.ExecuteNonQueryAsync();
            return;
        }

        await using var sql = new SqlConnection(Servers.SqlServerConnectionString);
        await sql.OpenAsync();
        await using var drop = sql.CreateCommand();
        drop.CommandText = $@"
IF OBJECT_ID('{MigratingItemsDbContext.SchemaName}.migrating_items') IS NOT NULL DROP TABLE {MigratingItemsDbContext.SchemaName}.migrating_items;
IF OBJECT_ID('{MigratingItemsDbContext.SchemaName}.migrating_items_unpartitioned') IS NOT NULL DROP TABLE {MigratingItemsDbContext.SchemaName}.migrating_items_unpartitioned;
IF OBJECT_ID('{WolverineSchema}.wolverine_tenant_partitions') IS NOT NULL DROP TABLE {WolverineSchema}.wolverine_tenant_partitions;
IF OBJECT_ID('{WolverineSchema}.wolverine_tenants') IS NOT NULL DROP TABLE {WolverineSchema}.wolverine_tenants;
IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'ps_migrating_items_tenant_ordinal') DROP PARTITION SCHEME ps_migrating_items_tenant_ordinal;
IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = 'pf_migrating_items_tenant_ordinal') DROP PARTITION FUNCTION pf_migrating_items_tenant_ordinal;";
        await drop.ExecuteNonQueryAsync();
    }
}

public class conjoined_partitioning_migration_with_postgresql : ConjoinedPartitioningMigrationCompliance
{
    public conjoined_partitioning_migration_with_postgresql() : base(DatabaseEngine.PostgreSQL)
    {
    }
}

public class conjoined_partitioning_migration_with_sqlserver : ConjoinedPartitioningMigrationCompliance
{
    public conjoined_partitioning_migration_with_sqlserver() : base(DatabaseEngine.SqlServer)
    {
    }
}
