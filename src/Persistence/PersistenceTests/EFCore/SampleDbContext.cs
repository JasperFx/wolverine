using Microsoft.EntityFrameworkCore;

namespace PersistenceTests.EFCore;

public class SampleDbContext : DbContext
{
    private readonly DbContextOptions<SampleDbContext> _options;

    public SampleDbContext(DbContextOptions<SampleDbContext> options) : base(options)
    {
        _options = options;
    }
    
    public DbSet<Item> Items { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Your normal EF Core mapping
        modelBuilder.Entity<Item>(map =>
        {
            map.ToTable("items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}