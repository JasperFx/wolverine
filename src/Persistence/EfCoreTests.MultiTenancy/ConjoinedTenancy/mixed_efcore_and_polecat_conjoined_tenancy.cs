using IntegrationTests;
using JasperFx;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Polecat;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

/// <summary>
/// GH-3531, scenario 2. Wolverine-managed conjoined EF Core tenancy and Polecat conjoined tenancy
/// sharing ONE physical SQL Server database -- the SQL Server twin of
/// <see cref="mixed_efcore_and_marten_conjoined_tenancy" />, unblocked once JasperFx/polecat#335
/// landed Polecat's own per-tenant managed partitioning.
///
/// <para>SQL Server makes the ownership question sharper than PostgreSQL does. Both engines partition
/// over a compact tenant ORDINAL allocated from a registry table, and partition functions are
/// database-global objects rather than schema-scoped ones, so two engines allocating ordinals and
/// splitting functions in one database is exactly where they could tread on each other. As with the
/// Marten twin, every assertion reads sys.* rather than either engine's own API, because an engine
/// reporting on its own partitions cannot show that it left the other alone.</para>
/// </summary>
[Collection("multi-tenancy")]
public class mixed_efcore_and_polecat_conjoined_tenancy : IAsyncLifetime
{
    private const string PolecatSchema = "mixed_polecat";
    private const string WolverineSchema = "mixed_sql_wolverine";
    private const string TenantRed = "red";
    private const string TenantBlue = "blue";

    // Partition function names are what Weasel (for EF) and Polecat derive from the table name
    private const string EfPartitionFunction = "pf_mixed_sql_items_tenant_ordinal";
    private const string PolecatPartitionFunction = "pf_pc_doc_mixedpolecatdoc_tenant_ordinal";

    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        await dropEverythingAsync();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<MixedSqlItemHandler>();

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, WolverineSchema);

                opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<MixedSqlItemsDbContext>(
                    (builder, connectionString) => builder.UseSqlServer(connectionString.Value),
                    AutoCreate.CreateOrUpdate,
                    tenancy => tenancy.PartitionPerTenant());

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = PolecatSchema;

                    // Polecat manages its OWN per-tenant partitions here -- the half of the scenario
                    // that matters: two engines each partitioning their own tables in one database,
                    // each from its own ordinal registry
                    m.Events.TenancyStyle = TenancyStyle.Conjoined;
                    m.Policies.PartitionMultiTenantedDocumentsUsingPolecatManagement();
                    m.Schema.For<MixedPolecatDoc>();
                });

                // NOTE: deliberately NOT .IntegrateWithWolverine(), for the same reason the Marten twin
                // leaves it off: these tests are about partition and tenant OWNERSHIP between the two
                // engines, which does not depend on who owns envelope storage.

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();
                opts.PublishAllMessages().Locally();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)theStore).Database.ApplyAllConfiguredChangesToDatabaseAsync(
            ct: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task each_engine_keeps_its_registry_in_its_own_schema()
    {
        var wolverineTables = await tablesInAsync(WolverineSchema);
        var polecatTables = await tablesInAsync(PolecatSchema);
        var efTables = await tablesInAsync(MixedSqlItemsDbContext.SchemaName);

        // Wolverine's conjoined bookkeeping -- the tenant registry and the ordinal registry
        wolverineTables.ShouldContain("wolverine_tenants");
        wolverineTables.ShouldContain("wolverine_tenant_partitions");

        // ...and it stays out of the other two schemas entirely
        polecatTables.ShouldNotContain("wolverine_tenants");
        polecatTables.ShouldNotContain("wolverine_tenant_partitions");
        efTables.ShouldNotContain("wolverine_tenants");
        efTables.ShouldNotContain("wolverine_tenant_partitions");

        // Polecat's ordinal registry lives in Polecat's schema and nowhere else
        polecatTables.ShouldContain("pc_tenant_partitions");
        wolverineTables.ShouldNotContain("pc_tenant_partitions");
        efTables.ShouldNotContain("pc_tenant_partitions");

        // Polecat's document storage stays in Polecat's schema, and the EF entity in EF's
        polecatTables.ShouldContain("pc_doc_mixedpolecatdoc");
        efTables.ShouldContain("mixed_sql_items");
        efTables.ShouldNotContain(x => x.StartsWith("pc_doc_"));
    }

    [Fact]
    public async Task registering_a_tenant_with_wolverine_splits_only_the_ef_tables_function()
    {
        var polecatBefore = await boundariesOfAsync(PolecatPartitionFunction);
        var polecatRegistryBefore = await registeredTenantsAsync(PolecatSchema, "pc_tenant_partitions");

        // Guard against a vacuous comparison of two empty lists: Polecat's function has to be there
        polecatBefore.ShouldNotBeEmpty();

        await theHost.AddWolverineManagedTenantsAsync<MixedSqlItemsDbContext>(TenantRed);

        (await registeredTenantsAsync(WolverineSchema, "wolverine_tenant_partitions")).ShouldContain(TenantRed);

        // The whole point: Wolverine split ITS function and left Polecat's function and registry as they were
        (await boundariesOfAsync(PolecatPartitionFunction)).ShouldBe(polecatBefore);
        (await registeredTenantsAsync(PolecatSchema, "pc_tenant_partitions")).ShouldBe(polecatRegistryBefore);
        (await registeredTenantsAsync(PolecatSchema, "pc_tenant_partitions")).ShouldNotContain(TenantRed);
    }

    [Fact]
    public async Task registering_a_tenant_with_polecat_splits_only_the_polecat_tables_function()
    {
        var efBefore = await boundariesOfAsync(EfPartitionFunction);
        var efRegistryBefore = await registeredTenantsAsync(WolverineSchema, "wolverine_tenant_partitions");

        // Same guard, the other way round: Weasel's function for the EF table has to be there
        efBefore.ShouldNotBeEmpty();

        await theStore.Advanced.AddPolecatManagedTenantsAsync(TestContext.Current.CancellationToken, TenantBlue);

        (await registeredTenantsAsync(PolecatSchema, "pc_tenant_partitions")).ShouldContain(TenantBlue);

        // ...and Polecat left the EF function and Wolverine's registry exactly as it found them
        (await boundariesOfAsync(EfPartitionFunction)).ShouldBe(efBefore);
        (await registeredTenantsAsync(WolverineSchema, "wolverine_tenant_partitions")).ShouldBe(efRegistryBefore);
        (await registeredTenantsAsync(WolverineSchema, "wolverine_tenant_partitions")).ShouldNotContain(TenantBlue);
    }

    [Fact]
    public async Task the_two_ordinal_registries_are_independent()
    {
        // Same tenant, registered with both engines: each allocates from ITS registry. The ordinals may
        // or may not coincide -- that is the point, nothing couples them -- but each registry must hold
        // exactly the tenants registered through it
        await theHost.AddWolverineManagedTenantsAsync<MixedSqlItemsDbContext>(TenantRed, TenantBlue);
        await theStore.Advanced.AddPolecatManagedTenantsAsync(TestContext.Current.CancellationToken, TenantRed);

        (await registeredTenantsAsync(WolverineSchema, "wolverine_tenant_partitions"))
            .OrderBy(x => x).ShouldBe([TenantBlue, TenantRed]);
        (await registeredTenantsAsync(PolecatSchema, "pc_tenant_partitions")).ShouldBe([TenantRed]);
    }

    [Fact]
    public async Task cross_tenant_isolation_holds_for_both_engines_in_one_database()
    {
        await theHost.AddWolverineManagedTenantsAsync<MixedSqlItemsDbContext>(TenantRed, TenantBlue);
        await theStore.Advanced.AddPolecatManagedTenantsAsync(TestContext.Current.CancellationToken, TenantRed,
            TenantBlue);

        var redItem = Guid.NewGuid();
        var blueItem = Guid.NewGuid();

        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync(TenantRed, new CreateMixedSqlItem(redItem, "red-item")));
        await theHost.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync(TenantBlue, new CreateMixedSqlItem(blueItem, "blue-item")));

        await using (var redSession = theStore.LightweightSession(new SessionOptions { TenantId = TenantRed }))
        {
            redSession.Store(new MixedPolecatDoc { Id = redItem, Name = "red-doc" });
            await redSession.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var blueSession = theStore.LightweightSession(new SessionOptions { TenantId = TenantBlue }))
        {
            blueSession.Store(new MixedPolecatDoc { Id = blueItem, Name = "blue-doc" });
            await blueSession.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // EF entities: each tenant sees only its own row
        var builder = theHost.Services.GetRequiredService<IDbContextBuilder<MixedSqlItemsDbContext>>();
        await using (var red = await builder.BuildAsync(TenantRed, TestContext.Current.CancellationToken))
        {
            (await red.Items.AnyAsync(x => x.Id == redItem, TestContext.Current.CancellationToken)).ShouldBeTrue();
            (await red.Items.AnyAsync(x => x.Id == blueItem, TestContext.Current.CancellationToken)).ShouldBeFalse();
        }

        // Polecat documents: the same isolation, from the same database
        await using (var red = theStore.QuerySession(new SessionOptions { TenantId = TenantRed }))
        {
            (await red.LoadAsync<MixedPolecatDoc>(redItem, TestContext.Current.CancellationToken)).ShouldNotBeNull();
            (await red.LoadAsync<MixedPolecatDoc>(blueItem, TestContext.Current.CancellationToken)).ShouldBeNull();
        }

        await using (var blue = theStore.QuerySession(new SessionOptions { TenantId = TenantBlue }))
        {
            (await blue.LoadAsync<MixedPolecatDoc>(blueItem, TestContext.Current.CancellationToken)).ShouldNotBeNull();
            (await blue.LoadAsync<MixedPolecatDoc>(redItem, TestContext.Current.CancellationToken)).ShouldBeNull();
        }
    }

    /// <summary>
    /// The boundary values of a partition function, straight from sys.partition_range_values -- neither
    /// engine's own bookkeeping. An empty list means the function does not exist (yet).
    /// </summary>
    private static async Task<IReadOnlyList<string>> boundariesOfAsync(string partitionFunction)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          select cast(prv.value as nvarchar(50))
                          from sys.partition_functions pf
                              join sys.partition_range_values prv on prv.function_id = pf.function_id
                          where pf.name = @name
                          order by prv.boundary_id;
                          """;
        cmd.Parameters.AddWithValue("name", partitionFunction);
        return await readStringsAsync(cmd);
    }

    private static async Task<IReadOnlyList<string>> registeredTenantsAsync(string schema, string registryTable)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select tenant_id from [{schema}].[{registryTable}] order by tenant_id;";
        return await readStringsAsync(cmd);
    }

    private static async Task<IReadOnlyList<string>> tablesInAsync(string schema)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "select t.name from sys.tables t join sys.schemas s on s.schema_id = t.schema_id where s.name = @schema order by t.name;";
        cmd.Parameters.AddWithValue("schema", schema);
        return await readStringsAsync(cmd);
    }

    private static async Task<IReadOnlyList<string>> readStringsAsync(SqlCommand cmd)
    {
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>
    /// Every table in the three schemas, then the two partition schemes and functions. Partition
    /// functions are database-global, so only the two this fixture creates are touched -- PolecatTests
    /// may be running its own against the same server.
    /// </summary>
    private static async Task dropEverythingAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        foreach (var schema in new[] { MixedSqlItemsDbContext.SchemaName, PolecatSchema, WolverineSchema })
        {
            foreach (var table in await tablesInAsync(schema))
            {
                await using var drop = conn.CreateCommand();
                drop.CommandText = $"drop table [{schema}].[{table}];";
                await drop.ExecuteNonQueryAsync();
            }
        }

        foreach (var function in new[] { EfPartitionFunction, PolecatPartitionFunction })
        {
            var scheme = "ps_" + function.Substring(3);
            await using var drop = conn.CreateCommand();
            drop.CommandText = $"""
                                if exists (select 1 from sys.partition_schemes where name = '{scheme}') drop partition scheme [{scheme}];
                                if exists (select 1 from sys.partition_functions where name = '{function}') drop partition function [{function}];
                                """;
            await drop.ExecuteNonQueryAsync();
        }
    }
}
