using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace PersistenceTests.EFCore;

public class SampleDbContext : DbContext
{
    public SampleDbContext(DbContextOptions<SampleDbContext> options) : base(options)
    {
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


public class SampleMappedDbContext : DbContext
{
    public SampleMappedDbContext(DbContextOptions<SampleMappedDbContext> options) : base(options)
    {
    }

    public DbSet<Item> Items { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.MapWolverineEnvelopeStorage();
        
        // Your normal EF Core mapping
        modelBuilder.Entity<Item>(map =>
        {
            map.ToTable("items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}