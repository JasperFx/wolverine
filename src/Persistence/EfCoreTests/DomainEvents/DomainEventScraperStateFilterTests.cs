using Microsoft.EntityFrameworkCore;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine.EntityFrameworkCore;

namespace EfCoreTests.DomainEvents;

/// <summary>
/// Unit tests verifying that DomainEventScraper only processes Added and Modified entities,
/// skipping Unchanged and Deleted ones (issue #2476 optimization).
/// </summary>
public class DomainEventScraperStateFilterTests
{
    private static DbContextOptions<ScraperTestDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<ScraperTestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    [Fact]
    public void change_tracker_filter_only_returns_added_and_modified_entries()
    {
        using var ctx = new ScraperTestDbContext(BuildOptions());

        var added = new Item { Id = Guid.CreateVersion7(), Name = "Added" };
        var modified = new Item { Id = Guid.CreateVersion7(), Name = "Modified" };
        var unchanged = new Item { Id = Guid.CreateVersion7(), Name = "Unchanged" };
        var deleted = new Item { Id = Guid.CreateVersion7(), Name = "Deleted" };

        ctx.Entry(added).State = EntityState.Added;
        ctx.Entry(modified).State = EntityState.Modified;
        ctx.Entry(unchanged).State = EntityState.Unchanged;
        ctx.Entry(deleted).State = EntityState.Deleted;

        // This mirrors the filter introduced in DomainEventScraper<T, TEvent>.ScrapeEvents
        var scraped = ctx.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified)
            .Select(x => x.Entity)
            .OfType<Item>()
            .ToList();

        scraped.Count.ShouldBe(2);
        scraped.ShouldContain(added);
        scraped.ShouldContain(modified);
        scraped.ShouldNotContain(unchanged);
        scraped.ShouldNotContain(deleted);
    }

    [Fact]
    public async Task domain_event_scraper_collects_events_from_added_and_modified_but_not_unchanged_or_deleted()
    {
        var options = BuildOptions();

        // Seed two items so they can be loaded in Unchanged / Deleted states
        using (var seed = new ScraperTestDbContext(options))
        {
            seed.Items.Add(new Item { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Name = "WillBeUnchanged" });
            seed.Items.Add(new Item { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Name = "WillBeDeleted" });
            await seed.SaveChangesAsync();
        }

        using var ctx = new ScraperTestDbContext(options);

        // Added – new entity not yet persisted
        var addedItem = new Item { Id = Guid.CreateVersion7(), Name = "Added" };
        ctx.Items.Add(addedItem);
        addedItem.Approve(); // raises ItemApproved event

        // Modified – load, change, and let EF detect it
        var modifiedItem = await ctx.Items.FindAsync(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        modifiedItem!.Approve(); // raises event AND sets Approved=true → Modified state

        // Unchanged – load but do not touch
        // (we manually add an event to the unchanged item to prove the scraper skips it)
        var unchangedItem = await ctx.Items.FindAsync(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        unchangedItem!.Publish(new ItemApproved(unchangedItem.Id)); // event added, but state stays Unchanged

        // Verify states are as expected
        ctx.Entry(addedItem).State.ShouldBe(EntityState.Added);
        ctx.Entry(modifiedItem).State.ShouldBe(EntityState.Modified);
        ctx.Entry(unchangedItem).State.ShouldBe(EntityState.Unchanged);

        // Collect the entities that the optimized scraper would target
        var scraped = ctx.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified)
            .Select(x => x.Entity)
            .OfType<IEntity>()
            .ToList();

        scraped.Count.ShouldBe(2);
        scraped.ShouldContain(addedItem);
        scraped.ShouldContain(modifiedItem);
        scraped.ShouldNotContain(unchangedItem);

        // Events from the two targeted entities
        var events = scraped.SelectMany(e => e.Events).ToList();
        events.Count.ShouldBe(2);
        events.OfType<ItemApproved>().ShouldContain(e => e.Id == addedItem.Id);
        events.OfType<ItemApproved>().ShouldContain(e => e.Id == modifiedItem.Id);
        events.OfType<ItemApproved>().ShouldNotContain(e => e.Id == unchangedItem.Id);
    }
}

// Minimal DbContext for these unit tests – no Wolverine envelope storage needed.
public class ScraperTestDbContext(DbContextOptions<ScraperTestDbContext> options) : DbContext(options)
{
    public DbSet<Item> Items { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Item>(map =>
        {
            map.ToTable("scraper_test_items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
            map.Property(x => x.Approved);
        });
    }
}
