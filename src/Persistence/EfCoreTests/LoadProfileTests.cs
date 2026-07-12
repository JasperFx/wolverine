using Microsoft.EntityFrameworkCore;
using Shouldly;
using Wolverine.EntityFrameworkCore;

namespace EfCoreTests;

public class LoadProfileTests
{
    private static DbContextOptions<ProfileDbContext> InMemory(string name) =>
        new DbContextOptionsBuilder<ProfileDbContext>().UseInMemoryDatabase(name).Options;

    private static async Task<(DbContextOptions<ProfileDbContext> options, Guid id)> SeededStore()
    {
        var options = InMemory(Guid.NewGuid().ToString());
        var id = Guid.NewGuid();

        await using var db = new ProfileDbContext(options);
        db.Orders.Add(new ProfileOrder
        {
            Id = id,
            Lines = { new ProfileLine { Id = Guid.NewGuid(), Product = "Widget" } }
        });
        await db.SaveChangesAsync();

        return (options, id);
    }

    [Fact]
    public async Task named_profile_applies_its_include_graph()
    {
        var (options, id) = await SeededStore();

        // Fresh context so navigation fixup can't mask the include.
        await using var db = new ProfileDbContext(options);
        var order = await EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            EfCoreLoadProfiles.QueryFor<ProfileOrder>(db, "full"), o => o.Id == id);

        order.ShouldNotBeNull();
        order.Lines.Count.ShouldBe(1);
    }

    [Fact]
    public async Task profile_without_include_loads_the_root_only()
    {
        var (options, id) = await SeededStore();

        await using var db = new ProfileDbContext(options);
        var order = await EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            EfCoreLoadProfiles.QueryFor<ProfileOrder>(db, "summary"), o => o.Id == id);

        order.ShouldNotBeNull();
        order.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task unknown_profile_throws()
    {
        var (options, _) = await SeededStore();

        await using var db = new ProfileDbContext(options);
        Should.Throw<InvalidOperationException>(() => EfCoreLoadProfiles.QueryFor<ProfileOrder>(db, "does-not-exist"));
    }

    public class ProfileOrder
    {
        public Guid Id { get; set; }
        public List<ProfileLine> Lines { get; set; } = new();
    }

    public class ProfileLine
    {
        public Guid Id { get; set; }
        public string? Product { get; set; }
    }

    public class ProfileDbContext : DbContext
    {
        public ProfileDbContext(DbContextOptions<ProfileDbContext> options) : base(options)
        {
        }

        public DbSet<ProfileOrder> Orders => Set<ProfileOrder>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProfileOrder>().HasMany(o => o.Lines).WithOne();

            modelBuilder.Entity<ProfileOrder>()
                .HasLoadProfile("summary", q => q)
                .HasLoadProfile("full", q => q.Include(o => o.Lines));
        }
    }
}
