using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Wolverine.Util;

namespace EfCoreTests.Bugs;

// The GH-3886 companion for LOCAL queues. Bug_3886_sticky_handlers_ancillary_store drives the same
// shape through external Rabbit queues, which is DurableReceiver; sticky handlers pinned to local
// queues go through DurableLocalQueue instead, a separate call site with its own copy of
// assignAncillaryStoreIfNeeded. Both had to start resolving the store by endpoint, so both need an
// end-to-end test that the resolved store is the one actually written to.

public record LocalStickyMessage(Guid Id);

[StickyHandler("local-sticky-a")]
[Storage(typeof(LocalStickyADbContext))]
public sealed class LocalStickyAHandler
{
    public void Handle(LocalStickyMessage message, LocalStickyADbContext db)
    {
        db.Models.Add(new LocalStickyAModel { Id = message.Id });
    }
}

[StickyHandler("local-sticky-b")]
[Storage(typeof(LocalStickyBDbContext))]
public sealed class LocalStickyBHandler
{
    public void Handle(LocalStickyMessage message, LocalStickyBDbContext db)
    {
        db.Models.Add(new LocalStickyBModel { Id = message.Id });
    }
}

public sealed class LocalStickyAModel
{
    public Guid Id { get; set; }
}

public sealed class LocalStickyBModel
{
    public Guid Id { get; set; }
}

public sealed class LocalStickyADbContext : DbContext
{
    public LocalStickyADbContext(DbContextOptions<LocalStickyADbContext> options) : base(options)
    {
    }

    public DbSet<LocalStickyAModel> Models => Set<LocalStickyAModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("local_sticky_a");
        modelBuilder.Entity<LocalStickyAModel>().ToTable("models");
    }
}

public sealed class LocalStickyBDbContext : DbContext
{
    public LocalStickyBDbContext(DbContextOptions<LocalStickyBDbContext> options) : base(options)
    {
    }

    public DbSet<LocalStickyBModel> Models => Set<LocalStickyBModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("local_sticky_b");
        modelBuilder.Entity<LocalStickyBModel>().ToTable("models");
    }
}

public class sticky_local_queue_ancillary_stores : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<LocalStickyAHandler>()
                    .IncludeType<LocalStickyBHandler>();

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                // The one message fans out to both sticky local queues, so the destination has to be
                // part of the inbox identity or the second queue's row collides with the first
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();
                opts.UseEntityFrameworkCoreTransactions();

                opts.Services.AddDbContextWithWolverineIntegration<LocalStickyADbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString), "local_sticky_a_wolverine");
                opts.Services.AddDbContextWithWolverineIntegration<LocalStickyBDbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString), "local_sticky_b_wolverine");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "local_sticky_main");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString,
                    "local_sticky_a_wolverine", MessageStoreRole.Ancillary).Enroll<LocalStickyADbContext>();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString,
                    "local_sticky_b_wolverine", MessageStoreRole.Ancillary).Enroll<LocalStickyBDbContext>();

                opts.Services.AddResourceSetupOnStartup();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task each_sticky_local_queue_persists_its_envelope_in_its_own_store()
    {
        var runtime = _host.GetRuntime();
        var messageTypeName = typeof(LocalStickyMessage).ToMessageTypeName();
        var id = Guid.NewGuid();

        await _host
            .TrackActivity()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new LocalStickyMessage(id));

        // The mark-as-handled write is asynchronous relative to the tracked session completing
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        var storeA = runtime.Stores!.FindAncillaryStore(typeof(LocalStickyADbContext));
        var storeB = runtime.Stores.FindAncillaryStore(typeof(LocalStickyBDbContext));

        var inA = (await storeA.Admin.AllIncomingAsync()).Where(x => x.MessageType == messageTypeName).ToArray();
        var inB = (await storeB.Admin.AllIncomingAsync()).Where(x => x.MessageType == messageTypeName).ToArray();
        var inMain = (await runtime.Storage.Admin.AllIncomingAsync())
            .Where(x => x.MessageType == messageTypeName).ToArray();

        inA.Length.ShouldBe(1, "The local-sticky-a delivery belongs in the LocalStickyA store");
        inB.Length.ShouldBe(1, "The local-sticky-b delivery belongs in the LocalStickyB store");
        inMain.ShouldBeEmpty("Neither delivery belongs in the main store");

        inA.Single().Destination.ShouldBe(new Uri("local://local-sticky-a"));
        inB.Single().Destination.ShouldBe(new Uri("local://local-sticky-b"));

        // Asserted by id, not row count: ResetResourceState clears Wolverine's stores but not the
        // application's own model tables, so counts accumulate across runs.
        var token = TestContext.Current.CancellationToken;
        await using var scope = _host.Services.CreateAsyncScope();

        (await scope.ServiceProvider.GetRequiredService<LocalStickyADbContext>()
            .Models.AnyAsync(x => x.Id == id, token)).ShouldBeTrue();
        (await scope.ServiceProvider.GetRequiredService<LocalStickyBDbContext>()
            .Models.AnyAsync(x => x.Id == id, token)).ShouldBeTrue();
    }
}
