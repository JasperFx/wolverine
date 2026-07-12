using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Wolverine.Http.Tests.LoadProfileOnly;

// Endpoints exercising named EF Core load profiles ([Entity(Profile = "...")] +
// modelBuilder.Entity<T>().HasLoadProfile(...)). Isolated in its own Marten-free assembly so the
// eager [Entity] parameter matching (which resolves the owning DbContext at discovery time) does
// not force LoadProfileDbContext onto the sibling GH-3353/3358/3374 hosts.

public class LoadProfileDbContext : DbContext
{
    public LoadProfileDbContext(DbContextOptions<LoadProfileDbContext> options) : base(options)
    {
    }

    public DbSet<ProfileOrder> Orders => Set<ProfileOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProfileOrder>(map =>
        {
            map.ToTable("profile_orders", "loadprofile");
            map.HasKey(x => x.Id);
            map.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId);
        });

        modelBuilder.Entity<ProfileOrderLine>(map =>
        {
            map.ToTable("profile_order_lines", "loadprofile");
            map.HasKey(x => x.Id);
        });

        // Named load profiles declared on the model — selected per call site with [Entity(Profile=...)].
        modelBuilder.Entity<ProfileOrder>()
            .HasLoadProfile("summary", q => q)                        // root-only
            .HasLoadProfile("full", q => q.Include(o => o.Lines));    // with children
    }
}

public class ProfileOrder
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<ProfileOrderLine> Lines { get; set; } = new();
}

public class ProfileOrderLine
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Product { get; set; } = string.Empty;
}

public sealed record ProfileLoadResponse(Guid Id, string Name, int LineCount);

public static class LoadProfileEndpoints
{
    [WolverineGet("/loadprofile/full/{id}")]
    public static ProfileLoadResponse Full([Entity(Profile = "full")] ProfileOrder order)
        => new(order.Id, order.Name, order.Lines.Count);

    [WolverineGet("/loadprofile/summary/{id}")]
    public static ProfileLoadResponse Summary([Entity(Profile = "summary")] ProfileOrder order)
        => new(order.Id, order.Name, order.Lines.Count);
}
