using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Wolverine.Attributes;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

// GH-3531, scenario 2. The EF half of a SQL Server database shared with Polecat. Its own schema for
// the same reason as the Marten twin: so the assertions can say WHICH engine owns a table instead of
// inferring it from a name.
public class MixedSqlItem : ITenanted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? TenantId { get; set; }
}

public record CreateMixedSqlItem(Guid Id, string Name);

[WolverineIgnore]
public class MixedSqlItemHandler
{
    public static void Handle(CreateMixedSqlItem command, MixedSqlItemsDbContext db)
    {
        db.Items.Add(new MixedSqlItem { Id = command.Id, Name = command.Name });
    }
}

public class MixedSqlItemsDbContext : DbContext
{
    public const string SchemaName = "mixed_sql_ef";

    public MixedSqlItemsDbContext(DbContextOptions<MixedSqlItemsDbContext> options) : base(options)
    {
    }

    public DbSet<MixedSqlItem> Items { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MixedSqlItem>(map =>
        {
            map.ToTable("mixed_sql_items", SchemaName);
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}

// The Polecat half of the same database.
public class MixedPolecatDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}
