using IntegrationTests;
using JasperFx.Descriptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.EntityFrameworkCore.Internals;

namespace EfCoreTests;

/// <summary>
/// GH-5339. Each <see cref="EntityDescriptor" /> carries the script that creates its table, read from the model
/// alone, the way <c>DocumentMappingDescriptor.Ddl</c> does for a document.
/// </summary>
public class entity_descriptor_ddl_5339
{
    [Fact]
    public void sql_server_entity_reports_its_table_keys_and_indexes()
    {
        var ddl = ddlFor(new DbContextOptionsBuilder<DdlOrdersDbContext>()
            .UseSqlServer(Servers.SqlServerConnectionString).Options, typeof(DdlOrder));

        ddl.ShouldContain("CREATE TABLE [ddl_5339].[Orders]");
        ddl.ShouldContain("CONSTRAINT [PK_Orders] PRIMARY KEY ([Id])");
        ddl.ShouldContain("CONSTRAINT [FK_Orders_Customers_CustomerId] FOREIGN KEY ([CustomerId])");
        ddl.ShouldContain("CREATE INDEX [IX_Orders_CustomerId] ON [ddl_5339].[Orders] ([CustomerId]);");
        ddl.ShouldContain("CREATE UNIQUE INDEX [IX_Orders_Reference] ON [ddl_5339].[Orders] ([Reference]);");

        // Only this entity's table, not the customer table it points at
        ddl.ShouldNotContain("CREATE TABLE [ddl_5339].[Customers]");
    }

    [Fact]
    public void postgresql_entity_reports_its_table_keys_and_indexes()
    {
        var ddl = ddlFor(new DbContextOptionsBuilder<DdlOrdersDbContext>()
            .UseNpgsql(Servers.PostgresConnectionString).Options, typeof(DdlOrder));

        ddl.ShouldContain("CREATE TABLE ddl_5339.\"Orders\"");
        ddl.ShouldContain("CONSTRAINT \"PK_Orders\" PRIMARY KEY (\"Id\")");
        ddl.ShouldContain("CONSTRAINT \"FK_Orders_Customers_CustomerId\" FOREIGN KEY (\"CustomerId\")");
        ddl.ShouldContain("CREATE INDEX \"IX_Orders_CustomerId\" ON ddl_5339.\"Orders\" (\"CustomerId\");");
        ddl.ShouldContain("CREATE UNIQUE INDEX \"IX_Orders_Reference\" ON ddl_5339.\"Orders\" (\"Reference\");");
        ddl.ShouldNotContain("CREATE TABLE ddl_5339.\"Customers\"");
    }

    [Fact]
    public void in_memory_provider_leaves_the_script_empty()
    {
        var usage = buildUsage(new DbContextOptionsBuilder<DdlOrdersDbContext>()
            .UseInMemoryDatabase("ddl_5339").Options);

        usage.Entities.Count.ShouldBe(2);
        usage.Entities.ShouldAllBe(x => x.Ddl == "");
    }

    private static string ddlFor(DbContextOptions<DdlOrdersDbContext> options, Type entityType)
    {
        return buildUsage(options).Entities.Single(x => x.EntityType.FullName == entityType.FullName).Ddl;
    }

    private static DbContextUsage buildUsage(DbContextOptions<DdlOrdersDbContext> options)
    {
        using var context = new DdlOrdersDbContext(options);
        using var services = new ServiceCollection().BuildServiceProvider();

        return DbContextUsageFactory.Build(new Uri("efcore://DdlOrdersDbContext"), context, "Single", services,
            tenantDatabases: null);
    }
}

public class DdlCustomer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class DdlOrder
{
    public Guid Id { get; set; }
    public string Reference { get; set; } = "";
    public int CustomerId { get; set; }
    public DdlCustomer Customer { get; set; } = null!;
    public decimal Total { get; set; }
}

public class DdlOrdersDbContext : DbContext
{
    public DdlOrdersDbContext(DbContextOptions<DdlOrdersDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ddl_5339");

        modelBuilder.Entity<DdlCustomer>().ToTable("Customers");

        modelBuilder.Entity<DdlOrder>(map =>
        {
            map.ToTable("Orders");
            map.Property(x => x.Total).HasPrecision(12, 2);
            map.HasIndex(x => x.Reference).IsUnique();
            map.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        });
    }
}
