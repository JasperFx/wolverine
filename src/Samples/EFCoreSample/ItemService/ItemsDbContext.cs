using Microsoft.EntityFrameworkCore;

namespace ItemService;

#region sample_ItemsDbContext

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
            map.Property(x => x.Name);
        });
    }
}

#endregion


#region sample_ItemsDbContext_NotIntegratedWithOutbox

public class ItemsDbContextWithoutOutbox : DbContext
{
    public ItemsDbContextWithoutOutbox(DbContextOptions<ItemsDbContextWithoutOutbox> options) : base(options)
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
            map.Property(x => x.Name);
        });
    }
}

#endregion

 