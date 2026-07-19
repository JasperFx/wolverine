using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Attributes;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

public class ConjoinedItem : ITenanted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? TenantId { get; set; }
}

// Deliberately NOT ITenanted -- proves the conventions only apply to marked entities
public class GlobalThing
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public record CreateConjoinedItem(Guid Id, string Name);

public record IncrementCounter(Guid Id);

public record StartCounter(Guid Id);

[WolverineIgnore]
public class ConjoinedItemHandler
{
    public static void Handle(CreateConjoinedItem command, ConjoinedItemsDbContext db)
    {
        db.Items.Add(new ConjoinedItem { Id = command.Id, Name = command.Name });
    }
}

[WolverineIgnore]
public class ConjoinedCounterSaga : Saga, ITenanted
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public string? TenantId { get; set; }

    public static ConjoinedCounterSaga Start(StartCounter command)
    {
        return new ConjoinedCounterSaga { Id = command.Id };
    }

    public void Handle(IncrementCounter command)
    {
        Count++;
    }
}

public class ConjoinedItemsDbContext : DbContext
{
    public ConjoinedItemsDbContext(DbContextOptions<ConjoinedItemsDbContext> options) : base(options)
    {
    }

    public DbSet<ConjoinedItem> Items { get; set; } = null!;
    public DbSet<GlobalThing> GlobalThings { get; set; } = null!;
    public DbSet<ConjoinedCounterSaga> Counters { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConjoinedItem>(map =>
        {
            map.ToTable("conjoined_items", "conjoined");
            map.HasKey(x => x.Id);
        });

        modelBuilder.Entity<GlobalThing>(map =>
        {
            map.ToTable("conjoined_global_things", "conjoined");
            map.HasKey(x => x.Id);
        });

        modelBuilder.Entity<ConjoinedCounterSaga>(map =>
        {
            map.ToTable("conjoined_counters", "conjoined");
            map.HasKey(x => x.Id);
        });
    }
}
