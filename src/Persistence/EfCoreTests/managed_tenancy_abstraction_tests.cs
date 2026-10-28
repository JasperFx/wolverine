using IntegrationTests;
using JasperFx;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace EfCoreTests.ManagedTenancyAbstractions;

// #5245. With managed EF Core tenancy, a WithDbContextAbstraction<IItemRepository, Context>() interface was
// resolved from the container -- the migrations-only registration, pinned to the default tenant -- so writes
// through it landed outside the tenant and outside the transaction. Every handler shape below runs in both
// transaction modes against two tenants of one host.
//
// GH-5262. In Lightweight mode a scraped domain event used to be saved AFTER the entity had committed, so a
// failed envelope write left the entity behind. See a_failed_event_envelope_write_rolls_back_the_entity.
//
// The DbContext is deliberately named Context, a name code generation has to rename.
public class managed_tenancy_abstraction_tests
{
    private static readonly TransactionMiddlewareMode[] Modes =
        [TransactionMiddlewareMode.Eager, TransactionMiddlewareMode.Lightweight];

    public static TheoryData<TransactionMiddlewareMode, HandlerShape> EveryShape()
    {
        var data = new TheoryData<TransactionMiddlewareMode, HandlerShape>();
        foreach (var mode in Modes)
        foreach (var shape in Enum.GetValues<HandlerShape>())
        {
            data.Add(mode, shape);
        }

        return data;
    }

    public static TheoryData<TransactionMiddlewareMode, HandlerShape, EventStyle> EveryShapeAndEventStyle()
    {
        var data = new TheoryData<TransactionMiddlewareMode, HandlerShape, EventStyle>();
        foreach (var mode in Modes)
        foreach (var shape in Enum.GetValues<HandlerShape>())
        foreach (var events in Enum.GetValues<EventStyle>())
        {
            data.Add(mode, shape, events);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public async Task writes_through_the_repository_land_in_the_right_tenant(TransactionMiddlewareMode mode,
        HandlerShape shape)
    {
        using var host = await startHostAsync(mode);

        var forRed = ItemCommand.For(shape);
        var forBlue = ItemCommand.For(shape);

        await host.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("red", forRed));
        await host.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("blue", forBlue));

        await using var red = await tenantDatabaseAsync(host, "red");
        await using var blue = await tenantDatabaseAsync(host, "blue");

        foreach (var id in forRed.WrittenIds)
        {
            (await red.TenantOfItemAsync(id)).ShouldBe("red");
            (await blue.HasItemAsync(id)).ShouldBeFalse();
        }

        foreach (var id in forBlue.WrittenIds)
        {
            (await blue.TenantOfItemAsync(id)).ShouldBe("blue");
            (await red.HasItemAsync(id)).ShouldBeFalse();
        }

        // The ItemCreated event was handled in the tenant that raised it
        (await red.TenantOfProjectionAsync(forRed.Id)).ShouldBe("red");
        (await blue.TenantOfProjectionAsync(forBlue.Id)).ShouldBe("blue");
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public async Task a_handler_failure_rolls_back_every_write(TransactionMiddlewareMode mode, HandlerShape shape)
    {
        using var host = await startHostAsync(mode);
        var command = ItemCommand.For(shape, fail: true);

        await Should.ThrowAsync<InvalidOperationException>(() => host.MessageBus().InvokeForTenantAsync("red", command));

        await using var red = await tenantDatabaseAsync(host, "red");
        foreach (var id in command.WrittenIds)
        {
            (await red.HasItemAsync(id)).ShouldBeFalse();
        }
    }

    [Theory]
    [MemberData(nameof(EveryShapeAndEventStyle))]
    public async Task a_failed_event_envelope_write_rolls_back_the_entity(TransactionMiddlewareMode mode,
        HandlerShape shape, EventStyle events)
    {
        using var host = await startHostAsync(mode);
        var command = ItemCommand.For(shape, events: events);

        await using var red = await tenantDatabaseAsync(host, "red");

        await using (await InboxInsertsRejected.StartAsync(red))
        {
            var exception = await Should.ThrowAsync<DbUpdateException>(() =>
                host.MessageBus().InvokeForTenantAsync("red", command));

            exception.InnerException.ShouldBeOfType<PostgresException>()
                .MessageText.ShouldBe(InboxInsertsRejected.Message);
        }

        foreach (var id in command.WrittenIds)
        {
            (await red.HasItemAsync(id)).ShouldBeFalse();
        }

        (await red.HasProjectionAsync(command.Id)).ShouldBeFalse();
    }

    private static Task<IHost> startHostAsync(TransactionMiddlewareMode mode)
    {
        return Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType<CreateItemHandler>()
                .IncludeType<CreateViaRepositoryHandler>()
                .IncludeType<StoreItemHandler>()
                .IncludeType<ItemCreatedHandler>();

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "abstract_tenant_messages");

            opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<Context>(
                (db, cs) => db.UseNpgsql(cs.Value), AutoCreate.CreateOrUpdate);

            // The application's own registrations of the abstractions, which used to win
            opts.Services.AddScoped<IItemRepository>(sp => sp.GetRequiredService<Context>());
            opts.Services.AddScoped<IItemQueries>(sp => sp.GetRequiredService<Context>());

            opts.UseEntityFrameworkCoreTransactions(mode)
                .WithDbContextAbstraction<IItemRepository, Context>()
                .WithDbContextAbstraction<IItemQueries, Context>();

            opts.PublishDomainEventsFromEntityFrameworkCore<Item, ItemCreated>(item => item.DequeueEvents());

            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();
            opts.PublishMessage<ItemCreated>().ToLocalQueue("abstract-tenant-projection");

            opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            opts.Services.AddResourceSetupOnStartup();
        }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Context> tenantDatabaseAsync(IHost host, string tenantId)
    {
        var builder = host.Services.GetRequiredService<IDbContextBuilder<Context>>();
        return await builder.BuildAsync(tenantId, TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// Makes every insert into the Wolverine inbox fail until disposed, so a test can watch what happens to the
/// entity writes that were meant to commit with it
/// </summary>
public class InboxInsertsRejected : IAsyncDisposable
{
    public const string Message = "Synthetic inbox failure";

    private readonly DbContext _db;
    private readonly string _schema;

    private InboxInsertsRejected(DbContext db, string schema)
    {
        _db = db;
        _schema = schema;
    }

    public static async Task<InboxInsertsRejected> StartAsync(DbContext db, string schema = "abstract_tenant_messages")
    {
        // The schema is always one of this test suite's own constants
        var sql = $"""
            CREATE OR REPLACE FUNCTION {schema}.reject_envelope() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION '{Message}'; END; $$;
            CREATE TRIGGER reject_envelope BEFORE INSERT ON {schema}.wolverine_incoming_envelopes
            FOR EACH ROW EXECUTE FUNCTION {schema}.reject_envelope();
            """;

        await db.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);

        return new InboxInsertsRejected(db, schema);
    }

    public async ValueTask DisposeAsync()
    {
        var sql = $"""
            DROP TRIGGER reject_envelope ON {_schema}.wolverine_incoming_envelopes;
            DROP FUNCTION {_schema}.reject_envelope();
            """;

        await _db.Database.ExecuteSqlRawAsync(sql);
    }
}

public enum HandlerShape
{
    // The repository abstraction, a second abstraction and the concrete DbContext, all in one handler
    WithContext,

    // Only the repository abstraction: no concrete DbContext anywhere in the handler's signature
    RepositoryOnly,

    // A repository write plus a returned Insert<Item> storage action in the same unit of work
    StorageAction
}

public enum EventStyle
{
    // The handler returns ItemCreated as a cascading message
    Returned,

    // The entity records ItemCreated and PublishDomainEventsFromEntityFrameworkCore() scrapes it
    Scraped
}

public interface IItemRepository
{
    void Add(Item item);
}

public interface IItemQueries;

public class Item : ITenanted
{
    private readonly List<ItemCreated> _events = [];

    public Guid Id { get; set; }
    public string? TenantId { get; set; }

    public void RecordCreation() => _events.Add(new ItemCreated(Id));

    public ItemCreated[] DequeueEvents()
    {
        var events = _events.ToArray();
        _events.Clear();
        return events;
    }

    // The event for a newly created item, raised in whichever way the command asks for
    public static (Item, ItemCreated?) Create(Guid id, EventStyle events)
    {
        var item = new Item { Id = id };
        if (events == EventStyle.Scraped)
        {
            item.RecordCreation();
            return (item, null);
        }

        return (item, new ItemCreated(id));
    }
}

public class Projection : ITenanted
{
    public Guid Id { get; set; }
    public string? TenantId { get; set; }
}

public class Context(DbContextOptions<Context> options) : DbContext(options), IItemRepository, IItemQueries
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Projection> Projections => Set<Projection>();

    public void Add(Item item) => Items.Add(item);

    public async Task<string?> TenantOfItemAsync(Guid id) =>
        (await Items.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken)).TenantId;

    public async Task<string?> TenantOfProjectionAsync(Guid id) =>
        (await Projections.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken)).TenantId;

    public Task<bool> HasItemAsync(Guid id) => Items.AnyAsync(x => x.Id == id, TestContext.Current.CancellationToken);

    public Task<bool> HasProjectionAsync(Guid id) =>
        Projections.AnyAsync(x => x.Id == id, TestContext.Current.CancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("abstract_tenant_data");
    }
}

public abstract record ItemCommand(Guid Id, bool Fail, EventStyle Events)
{
    // Every item id this command writes
    public virtual IEnumerable<Guid> WrittenIds => [Id];

    public static ItemCommand For(HandlerShape shape, bool fail = false, EventStyle events = EventStyle.Returned)
    {
        var id = Guid.NewGuid();
        return shape switch
        {
            HandlerShape.RepositoryOnly => new CreateViaRepository(id, fail, events),
            HandlerShape.StorageAction => new StoreItem(id, Guid.NewGuid(), fail, events),
            _ => new CreateItem(id, fail, events)
        };
    }

    public void FailIfAsked()
    {
        if (Fail) throw new InvalidOperationException("Synthetic handler failure");
    }
}

public record CreateItem(Guid Id, bool Fail, EventStyle Events) : ItemCommand(Id, Fail, Events);

public record CreateViaRepository(Guid Id, bool Fail, EventStyle Events) : ItemCommand(Id, Fail, Events);

public record StoreItem(Guid Id, Guid RepositoryId, bool Fail, EventStyle Events) : ItemCommand(Id, Fail, Events)
{
    public override IEnumerable<Guid> WrittenIds => [Id, RepositoryId];
}

public record ItemCreated(Guid Id);

[WolverineIgnore]
public class CreateItemHandler(IItemRepository repository)
{
    public ItemCreated? Handle(CreateItem command, IItemQueries queries, Context db)
    {
        var (item, created) = Item.Create(command.Id, command.Events);
        repository.Add(item);
        command.FailIfAsked();
        return created;
    }
}

[WolverineIgnore]
public class CreateViaRepositoryHandler(IItemRepository repository)
{
    public ItemCreated? Handle(CreateViaRepository command)
    {
        var (item, created) = Item.Create(command.Id, command.Events);
        repository.Add(item);
        command.FailIfAsked();
        return created;
    }
}

// Returning Insert<Item> exercises the entity overload of ApplyTransactionSupport
[WolverineIgnore]
public class StoreItemHandler(IItemRepository repository)
{
    public (Insert<Item>, ItemCreated?) Handle(StoreItem command)
    {
        repository.Add(new Item { Id = command.RepositoryId });
        command.FailIfAsked();

        var (item, created) = Item.Create(command.Id, command.Events);
        return (new Insert<Item>(item), created);
    }
}

[WolverineIgnore]
public class ItemCreatedHandler
{
    public static void Handle(ItemCreated message, Context db) => db.Projections.Add(new Projection { Id = message.Id });
}
