using Microsoft.EntityFrameworkCore;

namespace SharedPersistenceModels.Items;

public class ItemsDbContext : DbContext
{
    public ItemsDbContext(DbContextOptions<ItemsDbContext> options) : base(options)
    {
    }

    public DbSet<Item> Items { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Your normal EF Core mapping
        modelBuilder.Entity<Item>(map =>
        {
            map.ToTable("items", "mt_items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Id).HasColumnName("id");
            map.Property(x => x.Name).HasColumnName("name");
            map.Property(x => x.Approved).HasColumnName("approved");
        });
    }
}

// Let's get a couple versions
// 1. Use storage operations
// 2. Use DbContext directly