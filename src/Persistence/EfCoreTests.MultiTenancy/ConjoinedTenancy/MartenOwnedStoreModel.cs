using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Wolverine.Attributes;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

// GH-4044. A conjoined EF model in a database where Marten owns envelope storage. Its own
// DbContext type so the conjoined model cache cannot conflate it with the other batteries.
public class MartenOwnedItem : ITenanted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? TenantId { get; set; }
}

public record CreateMartenOwnedItem(Guid Id, string Name);

[WolverineIgnore]
public class MartenOwnedItemHandler
{
    public static void Handle(CreateMartenOwnedItem command, MartenOwnedItemsDbContext db)
    {
        db.Items.Add(new MartenOwnedItem { Id = command.Id, Name = command.Name });
    }
}

public class MartenOwnedItemsDbContext : DbContext
{
    public const string SchemaName = "gh4044_ef";

    public MartenOwnedItemsDbContext(DbContextOptions<MartenOwnedItemsDbContext> options) : base(options)
    {
    }

    public DbSet<MartenOwnedItem> Items { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MartenOwnedItem>(map =>
        {
            map.ToTable("marten_owned_items", SchemaName);
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}

// GH-4044. Its own type so the connection-string pin cannot reuse a cached model from the
// DbDataSource-configured context above
public class StringConfiguredDbContext : DbContext
{
    public StringConfiguredDbContext(DbContextOptions<StringConfiguredDbContext> options) : base(options)
    {
    }

    public DbSet<MartenOwnedItem> Items { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MartenOwnedItem>(map =>
        {
            map.ToTable("string_configured_items", MartenOwnedItemsDbContext.SchemaName);
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}
