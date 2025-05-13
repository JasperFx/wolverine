using Microsoft.EntityFrameworkCore;

namespace EfCoreTests.MultiTenancy;

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
            map.ToTable("items", "sample");
            map.HasKey(x => x.Id);
            map.Property(x => x.Id).HasColumnName("id");
            map.Property(x => x.Name).HasColumnName("name");
            map.Property(x => x.Approved).HasColumnName("approved");
        });
    }
}