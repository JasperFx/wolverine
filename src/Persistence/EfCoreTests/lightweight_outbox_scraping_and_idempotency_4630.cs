using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests;

/// <summary>
/// GH-4630. Behavioural coverage -- deliberately NOT codegen-string assertions -- for what
/// <see cref="TransactionMiddlewareMode.Lightweight" /> does to a *message handler*:
/// <list type="number">
/// <item>the cascaded messages have to ride the handler's own <c>SaveChangesAsync</c>,</item>
/// <item>registered domain event scrapers have to run,</item>
/// <item><c>AutoApplyTransactions(IdempotencyStyle.Eager)</c> has to actually check the inbox, and</item>
/// <item><c>EnableRetryOnFailure()</c> + Eager has to fail while the chain is being built rather than
/// on the first message.</item>
/// </list>
/// </summary>
public class lightweight_outbox_scraping_and_idempotency_4630 : IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        Handler4630.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private static Task<IHost> startHostAsync(TransactionMiddlewareMode mode, bool wolverineMapped = true,
        IdempotencyStyle? idempotency = null, bool durableLocalQueues = true,
        Action<WolverineOptions>? configure = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                if (wolverineMapped)
                {
                    opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString));
                }
                else
                {
                    opts.Services.AddDbContext<CleanDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString));
                }

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "lw4630");
                opts.UseEntityFrameworkCoreTransactions(mode);

                if (idempotency.HasValue)
                {
                    opts.Policies.AutoApplyTransactions(idempotency.Value);
                }
                else
                {
                    opts.Policies.AutoApplyTransactions();
                }

                if (durableLocalQueues)
                {
                    // The cascade has to be routed durably for the outbox to be involved at all
                    opts.Policies.UseDurableLocalQueues();
                }

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<Handler4630>()
                    .IncludeType<Cascade4630Handler>()
                    .IncludeType<Idempotent4630Handler>();

                configure?.Invoke(opts);

                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task lightweight_handler_writes_its_cascade_in_the_same_save_as_the_entity()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Lightweight);
        await host.RebuildAllEnvelopeStorageAsync();

        var id = Guid.CreateVersion7();
        await host.MessageBus().InvokeAsync(new Create4630Item(id, "Latte"), TestContext.Current.CancellationToken);

        // The cascade's envelope has to be part of the same EF Core unit of work as the Item. On a
        // Wolverine-mapped DbContext that means an envelope entity in the change tracker -- an
        // IncomingMessage here, because a durable *local* queue persists into the inbox table.
        Handler4630.TrackedAfterPublish.ShouldContain("IncomingMessage");
    }

    [Fact]
    public async Task eager_handler_writes_its_cascade_in_the_same_save_as_the_entity()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Eager);
        await host.RebuildAllEnvelopeStorageAsync();

        var id = Guid.CreateVersion7();
        await host.MessageBus().InvokeAsync(new Create4630Item(id, "Latte"), TestContext.Current.CancellationToken);

        // The control: Eager mode has always enlisted the DbContext, so this is what Lightweight has
        // to match.
        Handler4630.TrackedAfterPublish.ShouldContain("IncomingMessage");
    }

    [Fact]
    public async Task lightweight_handler_with_an_unmapped_dbcontext_writes_its_cascade_on_the_same_connection()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Lightweight, wolverineMapped: false);
        await host.RebuildAllEnvelopeStorageAsync();

        var id = Guid.CreateVersion7();
        await host.MessageBus().InvokeAsync(new Create4630Item(id, "Cortado"), TestContext.Current.CancellationToken);

        // An unmapped DbContext cannot track an OutgoingMessage entity, so the outbox write goes out as
        // raw ADO on the DbContext's own connection -- which means a transaction has to be open on it.
        Handler4630.HadOpenTransactionAfterPublish.ShouldBeTrue();

        // ...and something has to commit that transaction, or the Item write disappears with it.
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CleanDbContext>();
        (await db.Items.FindAsync([id], TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task eager_handler_that_throws_after_save_changes_keeps_neither_the_entity_nor_the_cascade()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Eager);
        await host.RebuildAllEnvelopeStorageAsync();

        var id = Guid.CreateVersion7();
        Handler4630.ThrowAfterSaveChanges = true;

        await Should.ThrowAsync<DeliberateFailure4630>(async () =>
            await host.MessageBus().InvokeAsync(new Create4630Item(id, "Mocha"), TestContext.Current.CancellationToken));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CleanDbContext>();
        (await db.Items.FindAsync([id], TestContext.Current.CancellationToken)).ShouldBeNull();

        Cascade4630Handler.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task lightweight_handler_that_throws_after_save_changes_keeps_the_entity_and_the_cascade_together()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Lightweight);
        await host.RebuildAllEnvelopeStorageAsync();

        var id = Guid.CreateVersion7();
        Handler4630.ThrowAfterSaveChanges = true;

        await Should.ThrowAsync<DeliberateFailure4630>(async () =>
            await host.MessageBus().InvokeAsync(new Create4630Item(id, "Mocha"), TestContext.Current.CancellationToken));

        // Lightweight has no explicit transaction, so the handler's own SaveChangesAsync really did
        // commit the Item. The point of GH-4630 is that the outgoing envelope went down with it, in
        // that same save, rather than in a second write that a later failure can skip.
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CleanDbContext>();
        (await db.Items.FindAsync([id], TestContext.Current.CancellationToken)).ShouldNotBeNull();

        Handler4630.TrackedAfterSaveChanges.ShouldContain("IncomingMessage:Unchanged");
    }

    [Fact]
    public async Task lightweight_handler_still_delivers_its_cascade()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Lightweight);
        await host.RebuildAllEnvelopeStorageAsync();

        var id = Guid.CreateVersion7();
        var tracked = await host.InvokeMessageAndWaitAsync(new Create4630Item(id, "Americano"));

        tracked.MessageSucceeded.SingleMessage<Item4630Created>().Id.ShouldBe(id);
    }

    [Fact]
    public async Task domain_events_are_scraped_in_lightweight_mode()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Lightweight,
            configure: opts =>
            {
                opts.PublishDomainEventsFromEntityFrameworkCore<Entity>(x => x.Events);
                opts.Discovery.IncludeType<Approve4630Handler>();
            });

        await host.RebuildAllEnvelopeStorageAsync();

        var id = Guid.CreateVersion7();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CleanDbContext>();
            db.Items.Add(new Item { Id = id, Name = "Flat White" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tracked = await host.InvokeMessageAndWaitAsync(new Approve4630Item(id));

        tracked.MessageSucceeded.SingleMessage<ItemApproved>().Id.ShouldBe(id);
    }

    [Fact]
    public async Task idempotency_check_is_emitted_in_lightweight_mode()
    {
        // Buffered local queues on purpose: the point here is the inbox check the middleware emits,
        // not the durable listener's own inbox row.
        using var host = await startHostAsync(TransactionMiddlewareMode.Lightweight,
            idempotency: IdempotencyStyle.Eager, durableLocalQueues: false);

        var tracked1 = await host.SendMessageAndWaitAsync(new Idempotent4630Message(Guid.CreateVersion7()));
        var sent = tracked1.Executed.SingleEnvelope<Idempotent4630Message>();

        var circuit = host.GetRuntime().Endpoints.FindListenerCircuit(sent.Destination!);

        var tracked2 = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(15.Seconds())
            .ExecuteAndWaitAsync(_ =>
            {
                sent.WasPersistedInInbox = false;
                sent.Attempts = 0;
                return circuit!.EnqueueDirectlyAsync([sent]);
            });

        tracked2.Discarded.SingleEnvelope<Idempotent4630Message>().ShouldNotBeNull();
    }

    [Fact]
    public async Task enable_retry_on_failure_with_eager_fails_at_codegen_with_the_remedy_named()
    {
        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;

                    opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString, o => o.EnableRetryOnFailure()));

                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "lw4630");
                    opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Eager);
                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType<Handler4630>()
                        .IncludeType<Cascade4630Handler>();
                }).StartAsync();
        });

        var message = ex.ToString();
        message.ShouldContain("EnableRetryOnFailure");
        message.ShouldContain(nameof(TransactionMiddlewareMode.Lightweight));
    }
}

public record Create4630Item(Guid Id, string Name);

public record Item4630Created(Guid Id);

public record Idempotent4630Message(Guid Id);

public record Approve4630Item(Guid Id);

public class DeliberateFailure4630() : Exception("Deliberate failure after SaveChangesAsync");

public class Handler4630
{
    public static bool ThrowAfterSaveChanges;
    public static readonly List<string> TrackedAfterPublish = [];
    public static readonly List<string> TrackedAfterSaveChanges = [];
    public static bool HadOpenTransactionAfterPublish;

    public static void Reset()
    {
        ThrowAfterSaveChanges = false;
        TrackedAfterPublish.Clear();
        TrackedAfterSaveChanges.Clear();
        HadOpenTransactionAfterPublish = false;
        Cascade4630Handler.Received.Clear();
    }

    public static async Task Handle(Create4630Item command, CleanDbContext db, IMessageBus bus,
        CancellationToken cancellation)
    {
        db.Items.Add(new Item { Id = command.Id, Name = command.Name });
        await bus.PublishAsync(new Item4630Created(command.Id));

        TrackedAfterPublish.Clear();
        TrackedAfterPublish.AddRange(db.ChangeTracker.Entries().Select(x => x.Entity.GetType().Name));
        HadOpenTransactionAfterPublish = db.Database.CurrentTransaction != null;

        if (!ThrowAfterSaveChanges) return;

        await db.SaveChangesAsync(cancellation);

        TrackedAfterSaveChanges.Clear();
        TrackedAfterSaveChanges.AddRange(db.ChangeTracker.Entries()
            .Select(x => $"{x.Entity.GetType().Name}:{x.State}"));

        throw new DeliberateFailure4630();
    }
}

public class Idempotent4630Handler
{
    // Deliberately no database side effect -- a replay that is NOT discarded should re-execute
    // cleanly, so the only thing the assertion can be reading is the inbox check.
    public static void Handle(Idempotent4630Message message, CleanDbContext db)
    {
    }
}

public class Cascade4630Handler
{
    public static readonly List<Guid> Received = [];

    public static void Handle(Item4630Created e) => Received.Add(e.Id);
}

public class Approve4630Handler
{
    public static async Task Handle(Approve4630Item command, CleanDbContext db, CancellationToken cancellation)
    {
        var item = await db.Items.FindAsync([command.Id], cancellation);
        item!.Approve();
    }

    public static void Handle(ItemApproved e)
    {
    }
}
