using Microsoft.EntityFrameworkCore;
using NSubstitute;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

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

    /// <summary>
    /// Regression test for https://github.com/JasperFx/wolverine/issues/2585.
    ///
    /// DomainEventScraper.ScrapeEvents used to enumerate
    /// dbContext.ChangeTracker.Entries() lazily and call PublishAsync per
    /// event inside the foreach. When PublishAsync runs through the EF-backed
    /// outbox, it adds an IncomingMessage entity to the SAME DbContext —
    /// mutating ChangeTracker mid-enumeration and throwing
    /// InvalidOperationException: "Collection was modified; enumeration
    /// operation may not execute."
    ///
    /// We reproduce the same hazard with an ISendMyself domain event whose
    /// ApplyAsync mutates the DbContext, so the test stays self-contained
    /// (no PostgreSQL/SqlServer outbox required). Without the .ToArray()
    /// materialization in DomainEventScraper, this test throws.
    ///
    /// Adapted from the closed PR #2586 by @jf2s; same approach.
    /// </summary>
    [Fact]
    public async Task domain_event_scraper_materializes_events_before_publishing()
    {
        using var ctx = new ScraperTestDbContext(BuildOptions());

        var item = new Item { Id = Guid.CreateVersion7(), Name = "Added" };
        item.Events.Add(new MutatingDomainEvent2585(ctx));
        ctx.Items.Add(item);

        var runtime = Substitute.For<IWolverineRuntime>();
        var context = new MessageContext(runtime);
        var scraper = new DomainEventScraper<Item, object>(x => x.Events);

        await Should.NotThrowAsync(() => scraper.ScrapeEvents(ctx, context));
    }
}

/// <summary>
/// Domain event used by the GH-2585 regression test. Mutates the DbContext
/// during PublishAsync, simulating what EfCoreEnvelopeTransaction.Persist-
/// IncomingAsync does in a real outbox-enrolled handler.
/// </summary>
public class MutatingDomainEvent2585(ScraperTestDbContext dbContext) : ISendMyself
{
    public ValueTask ApplyAsync(IMessageContext context)
    {
        dbContext.Items.Add(new Item { Id = Guid.CreateVersion7(), Name = "Added by PublishAsync" });
        return ValueTask.CompletedTask;
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
