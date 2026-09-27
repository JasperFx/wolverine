using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.EntityFrameworkCore;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests;

/// <summary>
///     GH-4635. Before this file there was not one non-flat <c>DbContext</c> model anywhere in the EF
///     suites or the samples — every mapped entity was scalar columns and, at most, a foreign key. Owned
///     types, complex types (table-split and, on EF 10, JSON), complex collections and collection
///     navigations are ordinary modelling, and they change what both halves of Wolverine's EF integration
///     see: <c>[Entity]</c>/<c>[FromEfCore]</c> load through the EF model, and
///     <c>UseEntityFrameworkCoreWolverineManagedMigrations()</c> translates that model into Weasel tables.
/// </summary>
/// <remarks>
///     <para>
///     This class runs the model against a schema <b>EF Core itself</b> created, which is the shape of an
///     application that keeps <c>dotnet ef database update</c> for its domain tables. The
///     Wolverine-managed migration path is in
///     <see cref="a_non_flat_model_under_wolverine_managed_migrations" />, and it does not currently
///     work — see there.
///     </para>
///     <para>
///     The EF 10-only halves — a complex property mapped <c>ToJson</c> and a <c>ComplexCollection</c> —
///     are behind <c>#if NET10_0_OR_GREATER</c>. They are compiled and run by the <c>CIEfCoreNet10</c>
///     lane added alongside this file; before that lane existed there was no way for them to run in CI.
///     </para>
/// </remarks>
[Collection("sqlserver")]
public class a_dbcontext_model_that_is_not_flat : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        await FacilitySchema.DropAsync();
        await FacilitySchema.CreateWithEfCoreAsync();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FacilityHandler));

                opts.Services.AddDbContextWithWolverineIntegration<FacilityDbContext>(
                    x => x.UseSqlServer(Servers.SqlServerConnectionString), "depots");

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "depots");
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();

                // Deliberately NOT UseEntityFrameworkCoreWolverineManagedMigrations(): the domain tables
                // were just created by EF Core, and the Weasel delta over them is exactly what
                // weasel_would_drop_the_table_split_complex_type_columns asserts. Applying that delta at
                // startup would drop the columns before any test ran.
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<Guid> seed()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FacilityDbContext>();

        db.Bays.RemoveRange(db.Bays);
        db.Facilities.RemoveRange(db.Facilities);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var facility = FacilitySchema.NewFacility();
        db.Facilities.Add(facility);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return facility.Id;
    }

    /// <summary>
    ///     Every non-flat member round trips through an ordinary EF read. The baseline: if this failed,
    ///     nothing below would mean anything.
    /// </summary>
    [Fact]
    public async Task every_non_flat_member_round_trips()
    {
        var id = await seed();

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FacilityDbContext>();

        var facility = await db.Facilities.Include(x => x.Bays)
            .SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);

        // OwnsOne
        facility.Address.City.ShouldBe("Topeka");
        // ComplexProperty, table-split
        facility.Footprint.Width.ShouldBe(40);
        facility.Footprint.Depth.ShouldBe(60);
        // collection navigation
        facility.Bays.Count.ShouldBe(2);

#if NET10_0_OR_GREATER
        // ComplexProperty().ToJson()
        facility.Location.Latitude.ShouldBe(39.0473m);
        // ComplexCollection
        facility.Contacts.Count.ShouldBe(2);
        facility.Contacts.Select(x => x.Kind).OrderBy(x => x).ToArray().ShouldBe(["email", "phone"]);
#endif
    }

    /// <summary>
    ///     <c>[Entity]</c> over a non-flat entity: the load goes through <c>FindAsync</c>, which returns the
    ///     owned and complex members populated, and the handler's <c>Update&lt;T&gt;</c> writes a mutation to
    ///     both of them back through the transactional middleware.
    /// </summary>
    [Fact]
    public async Task entity_attribute_loads_and_saves_the_non_flat_members()
    {
        var id = await seed();

        await _host.InvokeMessageAndWaitAsync(new RelocateFacility(id, "Wichita"));

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FacilityDbContext>();
        var facility = await db.Facilities.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);

        facility.Address.City.ShouldBe("Wichita");
        facility.Footprint.Width.ShouldBe(41);
    }

    /// <summary>
    ///     <c>[FromEfCore(Include = ...)]</c> across a collection navigation that hangs off an entity whose
    ///     other members are owned and complex. The control is the plain <c>[FromEfCore]</c> load, whose
    ///     navigation comes back empty.
    /// </summary>
    [Fact]
    public async Task from_ef_core_include_populates_the_navigation_on_a_non_flat_entity()
    {
        var id = await seed();

        var without = await _host.InvokeMessageAndWaitAsync(new CountBays(id));
        without.Sent.SingleMessage<BayCount>().Count.ShouldBe(0);

        var tracked = await _host.InvokeMessageAndWaitAsync(new CountBaysWithInclude(id));
        var read = tracked.Sent.SingleMessage<BayCount>();

        read.Count.ShouldBe(2);
        // ...and the owned and complex members survived the Include-shaped query, which is a different
        // query plan from the FindAsync a plain [FromEfCore] load uses
        read.City.ShouldBe("Topeka");
        read.Width.ShouldBe(40);
    }

    /// <summary>
    ///     A standalone (deliberately NOT batched) <c>IQueryPlan</c> over the same model, mutating what it
    ///     loaded and letting the transactional middleware save it. Batched plans over this model are
    ///     #4624's and are blocked on Weasel 9.35 being published, so they are left out here.
    /// </summary>
    [Fact]
    public async Task a_standalone_query_plan_can_mutate_and_save_a_non_flat_entity()
    {
        var id = await seed();

        await _host.InvokeMessageAndWaitAsync(new WidenFacilitiesNamed("Topeka"));

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FacilityDbContext>();
        var facility = await db.Facilities.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);

        facility.Footprint.Width.ShouldBe(50);
        facility.Address.Line1.ShouldBe("1 Main St");
    }

    /// <summary>
    ///     ...and it really was standalone. Asserted on the generated source because a batched and an
    ///     unbatched plan return the same results — the difference is only in the round trips, and in
    ///     whether #4624's Weasel dependency is in play.
    /// </summary>
    [Fact]
    public void the_standalone_query_plan_is_not_batched()
    {
        _host.GetRuntime().Handlers.HandlerFor<WidenFacilitiesNamed>();
        var chain = _host.GetRuntime().Handlers.ChainFor<WidenFacilitiesNamed>().ShouldNotBeNull();

        var code = chain.SourceCode.ShouldNotBeNull();
        code.ShouldNotContain("BatchedQuery");
    }

    /// <summary>
    ///     weasel#628, against the EF-created schema. The contract #4635 asks for is "no
    ///     <c>DROP COLUMN</c> is rendered": Weasel's translation of the EF model recognises every column
    ///     EF itself created, so the delta has nothing to remove.
    /// </summary>
    /// <remarks>
    ///     Fixed upstream in Weasel 9.35.1, and this test was flipped when Wolverine took that version
    ///     (GH-4624). Before it, Weasel's EF Core mapper could not see a table-split
    ///     <c>ComplexProperty</c>'s columns at all, so it reported the two columns EF created for
    ///     <c>Facility.Footprint</c> as <em>extras</em> -- columns in the database with nothing in the
    ///     model to justify them -- and a <c>CreateOrUpdate</c> apply issued <c>DROP COLUMN</c> for both.
    ///     An application on EF migrations plus Wolverine's resource setup lost those columns, and their
    ///     data, on its next boot. Owned types (<c>OwnsOne</c>) were always mapped correctly, which is why
    ///     the <c>address_*</c> columns were never at risk and are still asserted here as a control.
    /// </remarks>
    [Fact]
    public async Task no_drop_column_is_rendered_against_an_ef_created_schema()
    {
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FacilityDbContext>();
        var database = _host.Services.CreateDatabase(context, nameof(FacilityDbContext));

        var migration = await database.CreateMigrationAsync(CancellationToken.None);

        var facilities = migration.Deltas
            .OfType<TableDelta>()
            .Single(x => x.SchemaObject.Identifier.Name.EqualsIgnoreCase("facilities"));

        var writer = new StringWriter();
        facilities.WriteUpdate(new SqlServerMigrator(), writer);
        var sql = writer.ToString();

        // The whole point: the table-split complex type's columns are part of the model Weasel sees,
        // so there is nothing for the delta to remove.
        sql.ShouldNotContain("DROP COLUMN");

        // ...and the owned type's columns, which were never mis-mapped, are still left alone
        sql.ShouldNotContain("address_city");
    }
}

/// <summary>
///     GH-4635 / weasel#628. The same model through
///     <c>UseEntityFrameworkCoreWolverineManagedMigrations()</c> on a fresh database.
/// </summary>
/// <remarks>
///     <b>Pinned, not fixed, and reported rather than worked around.</b> The host starts clean and the
///     table it creates is missing the table-split complex type's columns entirely, so the very first
///     query against the entity fails with "Invalid column name". This is the same mapper blind spot as
///     <c>weasel_would_drop_the_table_split_complex_type_columns</c> seen from the create side instead of
///     the alter side, and it means Wolverine-managed migrations cannot currently be used with a
///     <c>ComplexProperty</c> at all. The fix is Weasel's, not Wolverine's.
/// </remarks>
[Collection("sqlserver")]
public class a_non_flat_model_under_wolverine_managed_migrations
{
    private static Task<IHost> startAsync()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.AddDbContextWithWolverineIntegration<FacilityDbContext>(
                    x => x.UseSqlServer(Servers.SqlServerConnectionString), "depots");

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "depots");
                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<string[]> columnsOfFacilities()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var names = new List<string>();
        await using var reader = await conn
            .CreateCommand(
                "select COLUMN_NAME from INFORMATION_SCHEMA.COLUMNS where TABLE_SCHEMA = 'depots' and TABLE_NAME = 'facilities'")
            .ExecuteReaderAsync(TestContext.Current.CancellationToken);

        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(await reader.GetFieldValueAsync<string>(0, TestContext.Current.CancellationToken));
        }

        return names.OrderBy(x => x).ToArray();
    }

    private static async Task<Dictionary<string, string>> columnTypesOfFacilities()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var types = new Dictionary<string, string>();
        await using var reader = await conn
            .CreateCommand(
                "select COLUMN_NAME, DATA_TYPE from INFORMATION_SCHEMA.COLUMNS where TABLE_SCHEMA = 'depots' and TABLE_NAME = 'facilities'")
            .ExecuteReaderAsync(TestContext.Current.CancellationToken);

        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            types[await reader.GetFieldValueAsync<string>(0, TestContext.Current.CancellationToken)] =
                await reader.GetFieldValueAsync<string>(1, TestContext.Current.CancellationToken);
        }

        return types;
    }

    /// <summary>
    ///     The half that works: the scalar columns, the key, and the owned type's table-split columns are
    ///     all created.
    /// </summary>
    [Fact]
    public async Task the_scalar_and_owned_type_columns_are_created()
    {
        await FacilitySchema.DropAsync();
        using var host = await startAsync();

        var columns = await columnsOfFacilities();

        columns.ShouldContain("Id");
        columns.ShouldContain("Name");
        columns.ShouldContain("address_line1");
        columns.ShouldContain("address_city");
        columns.ShouldContain("address_postal_code");
    }

    /// <summary>
    ///     weasel#628 from the create side, fixed in Weasel 9.35.1 (GH-4624). The two columns of the
    ///     table-split <c>ComplexProperty</c> are created along with everything else; before 9.35.1 they
    ///     were simply absent.
    /// </summary>
    [Fact]
    public async Task the_table_split_complex_type_columns_are_created()
    {
        await FacilitySchema.DropAsync();
        using var host = await startAsync();

        var columns = await columnsOfFacilities();

        columns.ShouldContain("footprint_width");
        columns.ShouldContain("footprint_depth");
    }

    /// <summary>
    ///     ...and the consequence that made it worth being loud about: before the fix the application
    ///     started cleanly, reported a healthy schema, and then failed on the first read of the entity.
    ///     It now reads.
    /// </summary>
    [Fact]
    public async Task and_so_a_query_over_the_entity_succeeds()
    {
        await FacilitySchema.DropAsync();
        using var host = await startAsync();

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FacilityDbContext>();

        await Should.NotThrowAsync(async () =>
            await db.Facilities.ToListAsync(TestContext.Current.CancellationToken));
    }

#if NET10_0_OR_GREATER
    /// <summary>
    ///     A complex property mapped with <c>ToJson()</c>, and a <c>ComplexCollection</c> -- both EF 10
    ///     only, which is why this is the one test in this class that stays behind the <c>#if</c>.
    /// </summary>
    /// <remarks>
    ///     Weasel's EF Core mapper used to render a JSON member as a <c>jsonb</c> column -- a PostgreSQL
    ///     type -- whatever the provider, so on SQL Server the CREATE TABLE was rejected outright and the
    ///     host never started. GH-4635 pinned that as broken; Weasel 9.35.1 fixed it by asking the
    ///     provider for its own JSON type, and this test was flipped when Wolverine took that version
    ///     (GH-4624). It is the reason the <c>CIEfCoreNet10</c> lane exists: nothing in the net9.0 half
    ///     can see it, because neither member can be expressed on EF 9.
    /// </remarks>
    [Fact]
    public async Task the_json_mapped_members_are_created_with_the_providers_own_json_type()
    {
        await FacilitySchema.DropAsync();

        // The host starting at all is the assertion -- it could not, before 9.35.1
        using var host = await startAsync();

        var types = await columnTypesOfFacilities();

        // Keys.ShouldContain rather than ShouldContainKey: the latter is ambiguous between Shouldly's
        // IDictionary and IReadOnlyDictionary overloads for a Dictionary<,> on net10.0
        types.Keys.ShouldContain("location");
        types.Keys.ShouldContain("contacts");

        // ...and emphatically not as Postgres' jsonb, which is what SQL Server rejected
        types["location"].ShouldNotBe("jsonb");
        types["contacts"].ShouldNotBe("jsonb");
    }
#endif
}

internal static class FacilitySchema
{
    public static async Task DropAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await conn.DropSchemaAsync("depots", TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    /// <summary>
    ///     The domain tables, built by EF Core from its own model — the "EF-migrated database" half of
    ///     #4635's ask. Wolverine's envelope tables are not in this context's model, so this creates the
    ///     two domain tables and nothing else; the message store creates its own.
    /// </summary>
    public static async Task CreateWithEfCoreAsync()
    {
        var builder = new DbContextOptionsBuilder<FacilityDbContext>()
            .UseSqlServer(Servers.SqlServerConnectionString);

        await using var db = new FacilityDbContext(builder.Options);
        await db.Database.GetService<IRelationalDatabaseCreator>()
            .CreateTablesAsync(TestContext.Current.CancellationToken);
    }

    public static Facility NewFacility()
    {
        var facility = new Facility
        {
            Id = Guid.NewGuid(),
            Name = "Topeka",
            Address = new PostalAddress { Line1 = "1 Main St", City = "Topeka", PostalCode = "66603" },
            Footprint = new Dimensions { Width = 40, Depth = 60 }
        };

#if NET10_0_OR_GREATER
        facility.Location = new GeoPoint { Latitude = 39.0473m, Longitude = -95.6752m };
        facility.Contacts =
        [
            new ContactMethod { Kind = "email", Value = "topeka@example.com" },
            new ContactMethod { Kind = "phone", Value = "555-0100" }
        ];
#endif

        facility.Bays.Add(new StorageBay { Id = Guid.NewGuid(), Label = "A1" });
        facility.Bays.Add(new StorageBay { Id = Guid.NewGuid(), Label = "A2" });

        return facility;
    }
}

public class Facility
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;

    /// <summary>Owned type — its own entity type in the model, table-split onto facilities.</summary>
    public PostalAddress Address { get; set; } = new();

    /// <summary>Complex type, table-split. Not an entity type at all, and no key of its own.</summary>
    public Dimensions Footprint { get; set; } = new();

#if NET10_0_OR_GREATER
    /// <summary>Complex type mapped to a JSON column. EF Core 10.</summary>
    public GeoPoint Location { get; set; } = new();

    /// <summary>Complex collection. EF Core 10.</summary>
    public List<ContactMethod> Contacts { get; set; } = [];
#endif

    /// <summary>Ordinary collection navigation, for Include.</summary>
    public List<StorageBay> Bays { get; set; } = [];
}

public class PostalAddress
{
    public string Line1 { get; set; } = "";
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
}

public class Dimensions
{
    public int Width { get; set; }
    public int Depth { get; set; }
}

#if NET10_0_OR_GREATER
public class GeoPoint
{
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
}

public class ContactMethod
{
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
}
#endif

public class StorageBay
{
    public Guid Id { get; set; }
    public Guid FacilityId { get; set; }
    public string Label { get; set; } = null!;
}

public class FacilityDbContext : DbContext
{
    public FacilityDbContext(DbContextOptions<FacilityDbContext> options) : base(options)
    {
    }

    public DbSet<Facility> Facilities { get; set; } = null!;
    public DbSet<StorageBay> Bays { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Facility>(map =>
        {
            map.ToTable("facilities", "depots");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);

            map.OwnsOne(x => x.Address, address =>
            {
                address.Property(x => x.Line1).HasColumnName("address_line1");
                address.Property(x => x.City).HasColumnName("address_city");
                address.Property(x => x.PostalCode).HasColumnName("address_postal_code");
            });

            map.ComplexProperty(x => x.Footprint, footprint =>
            {
                footprint.IsRequired();
                footprint.Property(x => x.Width).HasColumnName("footprint_width");
                footprint.Property(x => x.Depth).HasColumnName("footprint_depth");
            });

#if NET10_0_OR_GREATER
            map.ComplexProperty(x => x.Location, location =>
            {
                location.IsRequired();
                location.ToJson("location");
            });

            map.ComplexCollection(x => x.Contacts, contacts => contacts.ToJson("contacts"));
#endif

            map.HasMany(x => x.Bays).WithOne().HasForeignKey(x => x.FacilityId);
        });

        modelBuilder.Entity<StorageBay>(map =>
        {
            map.ToTable("storage_bays", "depots");
            map.HasKey(x => x.Id);
            map.Property(x => x.Label);
        });
    }
}

public record RelocateFacility(Guid Id, string City);

public record CountBays(Guid Id);

public record CountBaysWithInclude(Guid Id);

public record BayCount(int Count, string City, int Width);

public record WidenFacilitiesNamed(string Name);

[WolverineIgnore]
public static class FacilityHandler
{
    public static Update<Facility> Handle(RelocateFacility command, [Entity] Facility facility)
    {
        facility.Address.City = command.City;
        facility.Footprint.Width += 1;
        return Storage.Update(facility);
    }

    public static BayCount Handle(CountBays command, [FromEfCore] Facility facility)
        => count(facility);

    public static BayCount Handle(CountBaysWithInclude command, [FromEfCore(Include = "Bays")] Facility facility)
        => count(facility);

    public static void Handle(BayCount count)
    {
    }

    public static async Task Handle(WidenFacilitiesNamed command, FacilityDbContext db, CancellationToken token)
    {
        var facilities = await db.QueryByPlanAsync(new FacilitiesNamed(command.Name), token);

        foreach (var facility in facilities)
        {
            facility.Footprint.Width = 50;
        }
    }

    private static BayCount count(Facility facility)
        => new(facility.Bays.Count, facility.Address.City, facility.Footprint.Width);
}

public class FacilitiesNamed(string name) : QueryListPlan<FacilityDbContext, Facility>
{
    public override IQueryable<Facility> Query(FacilityDbContext db)
        => db.Facilities.Where(x => x.Name == name);
}
