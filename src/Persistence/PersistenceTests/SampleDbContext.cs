using Microsoft.EntityFrameworkCore;

namespace PersistenceTests;

public class SampleDbContext : DbContext
{
    public SampleDbContext(DbContextOptions<SampleDbContext> options) : base(options)
    {
    }

    public DbSet<Item> Items { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Your normal EF Core mapping
        modelBuilder.Entity<Item>(map =>
        {
            map.ToTable("items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}

public class Item
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}
