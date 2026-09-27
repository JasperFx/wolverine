using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

/// <summary>
///     Model for the GH-4629 / GH-4632 set-based operation tests under conjoined multi-tenancy.
///     Deliberately separate from <see cref="ConjoinedItem" /> so this branch and the concurrent
///     <c>[Entity]</c> work never edit the same test model.
/// </summary>
public class ScopedItem : ITenanted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public bool Archived { get; set; }
    public string? TenantId { get; set; }
}

public class ScopedItemsDbContext : DbContext
{
    public ScopedItemsDbContext(DbContextOptions<ScopedItemsDbContext> options) : base(options)
    {
    }

    public DbSet<ScopedItem> Items { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScopedItem>(map =>
        {
            map.ToTable("scoped_items", "conjoined_ops");
            map.HasKey(x => x.Id);
        });
    }
}

public record CreateScopedItem(Guid Id, string Name);

public record ArchiveScopedItem(Guid Id);

public record MoveScopedItemToTenant(Guid Id, string Destination);

public record PurgeScopedItemIgnoringFilters(Guid Id);

[WolverineIgnore]
public class ScopedItemHandler
{
    public static void Handle(CreateScopedItem command, ScopedItemsDbContext db)
    {
        db.Items.Add(new ScopedItem { Id = command.Id, Name = command.Name });
    }

    #region sample_conjoined_efcore_op

    public static EfCoreOp Handle(ArchiveScopedItem command)
    {
        return EfCoreOps.ExecuteUpdate<ScopedItem>(x => x.Id == command.Id,
            setters => setters.SetProperty(x => x.Archived, true));
    }

    #endregion

    public static EfCoreOp Handle(MoveScopedItemToTenant command)
    {
        return EfCoreOps.ExecuteUpdate<ScopedItem>(x => x.Id == command.Id,
            setters => setters.SetProperty(x => x.TenantId, command.Destination));
    }

    public static EfCoreOp Handle(PurgeScopedItemIgnoringFilters command)
    {
        return EfCoreOps.ExecuteDelete<ScopedItem>(x => x.Id == command.Id).IgnoreQueryFilters();
    }
}
