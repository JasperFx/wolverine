using Microsoft.EntityFrameworkCore;

namespace WolverineWebApi;

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
            map.ToTable("items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Id).HasColumnName("id");
            map.Property(x => x.Name).HasColumnName("name");
        });
    }
}

public class Item
{
    public string Name { get; set; }
    public Guid Id { get; set; }
}

public class CreateItemCommand
{
    public string Name { get; set; }
}

public class ItemCreated
{
    public Guid Id { get; set; }
}

public class ItemCreatedHandler
{
    public void Handle(ItemCreated @event)
    {
        Console.WriteLine("You created a new item with id " + @event.Id);
    }
}