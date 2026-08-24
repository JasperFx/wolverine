using IntegrationTests;
using JasperFx;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Marten;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

/// <summary>
/// GH-3531. Wolverine-managed conjoined EF Core tenancy and Marten conjoined tenancy sharing ONE
/// physical Postgres database, which is what a real Critter Stack app hits and what every existing
/// conjoined battery avoids by giving EF the database to itself.
///
/// <para>The question these ask is ownership: two engines each manage their own partitions and their
/// own tenant registry against the same server, and neither may create, drop or claim the other's.
/// Deliberately asserted against pg_catalog rather than against either engine's own API, because an
/// engine reporting on its own partitions cannot show that it left the other alone.</para>
/// </summary>
[Collection("multi-tenancy")]
public class mixed_efcore_and_marten_conjoined_tenancy : IAsyncLifetime
{
    private const string MartenSchema = "mixed_marten";
    private const string WolverineSchema = "mixed_wolverine";
    private const string TenantRed = "red";
    private const string TenantBlue = "blue";

    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    private IConjoinedTenantPartitions<MixedItemsDbContext> thePartitions = null!;

    public async ValueTask InitializeAsync()
    {
        await dropSchemasAsync();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<MixedItemHandler>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, WolverineSchema);

                opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<MixedItemsDbContext>(
                    (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                    AutoCreate.CreateOrUpdate,
                    tenancy => tenancy.PartitionPerTenant());

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = MartenSchema;
                    m.DisableNpgsqlLogging = true;
                    // Marten manages its OWN per-tenant partitions here, which is the half of the
                    // scenario that matters: two engines each partitioning their own tables in one DB
                    m.Policies.PartitionMultiTenantedDocumentsUsingMartenManagement(MartenSchema);

                    m.Schema.For<MixedDoc>().MultiTenanted();
                }).ApplyAllDatabaseChangesOnStartup();

                // NOTE: deliberately NOT .IntegrateWithWolverine() here -- see GH-3531. Handing the
                // Wolverine message store to Marten leaves the conjoined EF builder unable to resolve
                // a connection string ("Unable to determine the database connection string for the
                // conjoined multi-tenanted DbContext"), because it reads it from the message store's
                // database settings and Marten's store does not surface one. That interaction is a
                // finding in its own right; these tests are about partition and tenant OWNERSHIP
                // between the two engines, which does not depend on who owns envelope storage.

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();
                opts.PublishAllMessages().Locally();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        thePartitions = theHost.Services.GetRequiredService<IConjoinedTenantPartitions<MixedItemsDbContext>>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static async Task dropSchemasAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"DROP SCHEMA IF EXISTS {MixedItemsDbContext.SchemaName} CASCADE; " +
            $"DROP SCHEMA IF EXISTS {MartenSchema} CASCADE; " +
            $"DROP SCHEMA IF EXISTS {WolverineSchema} CASCADE;";
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task each_engine_keeps_its_control_tables_in_its_own_schema()
    {
        var wolverineTables = await tablesInAsync(WolverineSchema);
        var martenTables = await tablesInAsync(MartenSchema);
        var efTables = await tablesInAsync(MixedItemsDbContext.SchemaName);

        // Wolverine's conjoined bookkeeping -- the tenant registry and the partition control table
        wolverineTables.ShouldContain("wolverine_tenants");
        wolverineTables.ShouldContain("wolverine_tenant_partitions");

        // ...and it stays out of the other two schemas entirely
        martenTables.ShouldNotContain("wolverine_tenants");
        martenTables.ShouldNotContain("wolverine_tenant_partitions");
        efTables.ShouldNotContain("wolverine_tenants");
        efTables.ShouldNotContain("wolverine_tenant_partitions");

        // Marten's own partition control table -- the counterpart the issue names -- lives in
        // Marten's schema and nowhere else
        martenTables.ShouldContain("mt_tenant_partitions");
        wolverineTables.ShouldNotContain("mt_tenant_partitions");
        efTables.ShouldNotContain("mt_tenant_partitions");

        // Marten's document storage stays in Marten's schema, and the EF entity in EF's
        martenTables.ShouldContain(x => x.StartsWith("mt_doc_"));
        efTables.ShouldContain("mixed_items");
        efTables.ShouldNotContain(x => x.StartsWith("mt_doc_"));
    }

    [Fact]
    public async Task registering_a_tenant_with_wolverine_partitions_only_the_ef_table()
    {
        var martenBefore = await partitionsOfAsync(MartenSchema, "mt_doc_mixeddoc");

        await theHost.AddWolverineManagedTenantsAsync<MixedItemsDbContext>(TenantRed);

        var efAfter = await partitionsOfAsync(MixedItemsDbContext.SchemaName, "mixed_items");
        efAfter.ShouldContain(x => x.Contains(TenantRed));

        // The whole point: Wolverine created a partition on ITS table and left Marten's alone
        var martenAfter = await partitionsOfAsync(MartenSchema, "mt_doc_mixeddoc");
        martenAfter.ShouldBe(martenBefore);
        martenAfter.ShouldNotContain(x => x.Contains(TenantRed));
    }

    [Fact]
    public async Task registering_a_tenant_with_marten_does_not_partition_the_ef_table()
    {
        var efBefore = await partitionsOfAsync(MixedItemsDbContext.SchemaName, "mixed_items");

        await theStore.Advanced.AddMartenManagedTenantsAsync(TestContext.Current.CancellationToken,
            new Dictionary<string, string> { [TenantBlue] = TenantBlue });

        var martenAfter = await partitionsOfAsync(MartenSchema, "mt_doc_mixeddoc");
        martenAfter.ShouldContain(x => x.Contains(TenantBlue));

        // ...and Marten left the EF table exactly as it found it
        var efAfter = await partitionsOfAsync(MixedItemsDbContext.SchemaName, "mixed_items");
        efAfter.ShouldBe(efBefore);
        efAfter.ShouldNotContain(x => x.Contains(TenantBlue));
    }

    [Fact]
    public async Task cross_tenant_isolation_holds_for_both_engines_in_one_database()
    {
        await theHost.AddWolverineManagedTenantsAsync<MixedItemsDbContext>(TenantRed, TenantBlue);
        await theStore.Advanced.AddMartenManagedTenantsAsync(TestContext.Current.CancellationToken,
            new Dictionary<string, string> { [TenantRed] = TenantRed, [TenantBlue] = TenantBlue });

        var redItem = Guid.NewGuid();
        var blueItem = Guid.NewGuid();

        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync(TenantRed, new CreateMixedItem(redItem, "red-item")));
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync(TenantBlue, new CreateMixedItem(blueItem, "blue-item")));

        await using (var redSession = theStore.LightweightSession(TenantRed))
        {
            redSession.Store(new MixedDoc { Id = redItem, Name = "red-doc" });
            await redSession.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var blueSession = theStore.LightweightSession(TenantBlue))
        {
            blueSession.Store(new MixedDoc { Id = blueItem, Name = "blue-doc" });
            await blueSession.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // EF entities: each tenant sees only its own row
        var builder = theHost.Services.GetRequiredService<IDbContextBuilder<MixedItemsDbContext>>();
        await using (var red = await builder.BuildAsync(TenantRed, TestContext.Current.CancellationToken))
        {
            // Both Marten and EF Core ship an AnyAsync extension, and this file sees both -- qualify
            // so the EF one is unambiguously the one querying an EF DbSet
            (await EntityFrameworkQueryableExtensions.AnyAsync(
                red.Items, x => x.Id == redItem, TestContext.Current.CancellationToken)).ShouldBeTrue();
            (await EntityFrameworkQueryableExtensions.AnyAsync(
                red.Items, x => x.Id == blueItem, TestContext.Current.CancellationToken)).ShouldBeFalse();
        }

        // Marten documents: the same isolation, from the same database
        await using (var red = theStore.QuerySession(TenantRed))
        {
            (await red.LoadAsync<MixedDoc>(redItem, TestContext.Current.CancellationToken)).ShouldNotBeNull();
            (await red.LoadAsync<MixedDoc>(blueItem, TestContext.Current.CancellationToken)).ShouldBeNull();
        }

        await using (var blue = theStore.QuerySession(TenantBlue))
        {
            (await blue.LoadAsync<MixedDoc>(blueItem, TestContext.Current.CancellationToken)).ShouldNotBeNull();
            (await blue.LoadAsync<MixedDoc>(redItem, TestContext.Current.CancellationToken)).ShouldBeNull();
        }
    }

    /// <summary>
    /// Child partitions of a table, straight from pg_catalog -- neither engine's own bookkeeping.
    /// </summary>
    private static async Task<IReadOnlyList<string>> partitionsOfAsync(string schema, string table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          select child.relname
                          from pg_inherits
                             join pg_class parent on pg_inherits.inhparent = parent.oid
                             join pg_class child on pg_inherits.inhrelid = child.oid
                             join pg_namespace n on parent.relnamespace = n.oid
                          where n.nspname = :schema and parent.relname = :table
                          order by child.relname;
                          """;
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", table);

        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<IReadOnlyList<string>> tablesInAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "select tablename from pg_tables where schemaname = :schema order by tablename;";
        cmd.Parameters.AddWithValue("schema", schema);

        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }
}
