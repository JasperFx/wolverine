using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Wolverine.Attributes;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

// GH-3531. The EF half of a database shared with Marten. Deliberately in its own schema so the
// assertions can say WHICH engine owns a given table rather than inferring it from a name.
public class MixedItem : ITenanted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? TenantId { get; set; }
}

public record CreateMixedItem(Guid Id, string Name);

[WolverineIgnore]
public class MixedItemHandler
{
    public static void Handle(CreateMixedItem command, MixedItemsDbContext db)
    {
        db.Items.Add(new MixedItem { Id = command.Id, Name = command.Name });
    }
}

public class MixedItemsDbContext : DbContext
{
    public const string SchemaName = "mixed_ef";

    public MixedItemsDbContext(DbContextOptions<MixedItemsDbContext> options) : base(options)
    {
    }

    public DbSet<MixedItem> Items { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MixedItem>(map =>
        {
            map.ToTable("mixed_items", SchemaName);
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}

// The Marten half of the same database.
public class MixedDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}
