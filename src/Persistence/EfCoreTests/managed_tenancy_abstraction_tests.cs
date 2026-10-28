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

public class managed_tenancy_abstraction_tests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(TransactionMiddlewareMode.Eager, HandlerShape.WithContext, false)]
    [InlineData(TransactionMiddlewareMode.Lightweight, HandlerShape.WithContext, false)]
    [InlineData(TransactionMiddlewareMode.Eager, HandlerShape.StorageAction, false)]
    [InlineData(TransactionMiddlewareMode.Lightweight, HandlerShape.StorageAction, false)]
    [InlineData(TransactionMiddlewareMode.Eager, HandlerShape.RepositoryOnly, false)]
    [InlineData(TransactionMiddlewareMode.Lightweight, HandlerShape.RepositoryOnly, false)]
    [InlineData(TransactionMiddlewareMode.Eager, HandlerShape.WithContext, true)]
    [InlineData(TransactionMiddlewareMode.Eager, HandlerShape.StorageAction, true)]
    [InlineData(TransactionMiddlewareMode.Eager, HandlerShape.RepositoryOnly, true)]
    public async Task repository_and_transaction_share_the_tenant_context(TransactionMiddlewareMode mode, HandlerShape shape, bool automaticEvents)
    {
        var storageAction = shape == HandlerShape.StorageAction;
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Discovery.DisableConventionalDiscovery().IncludeType<CreateItemHandler>().IncludeType<StoreItemHandler>().IncludeType<CreateViaRepositoryHandler>().IncludeType<ItemCreatedHandler>();
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "abstract_tenant_messages");
            opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<Context>(
                (db, cs) => db.UseNpgsql(cs.Value), AutoCreate.CreateOrUpdate);
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

        var repositoryIds = new Dictionary<Guid, Guid>();
        object command(Guid itemId, bool fail = false)
        {
            var repositoryId = repositoryIds[itemId] = Guid.NewGuid();
            return shape switch
            {
                HandlerShape.StorageAction => new StoreItem(itemId, repositoryId, fail, automaticEvents),
                HandlerShape.RepositoryOnly => new CreateViaRepository(itemId, fail, automaticEvents),
                _ => new CreateItem(itemId, fail, automaticEvents)
            };
        }

        var builder = host.Services.GetRequiredService<IDbContextBuilder<Context>>();
        var id = Guid.NewGuid();
        var firstCommand = command(id);
        await host.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("red", firstCommand));
        output.WriteLine(host.GetRuntime().Handlers.ChainFor(firstCommand.GetType())!.SourceCode!);
        await using var red = await builder.BuildAsync("red", TestContext.Current.CancellationToken);
        await using var blue = await builder.BuildAsync("blue", TestContext.Current.CancellationToken);
        (await red.Items.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken)).TenantId.ShouldBe("red");
        (await red.Projections.SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken)).TenantId.ShouldBe("red");
        (await blue.Items.AnyAsync(x => x.Id == id, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await blue.Projections.AnyAsync(x => x.Id == id, TestContext.Current.CancellationToken)).ShouldBeFalse();

        if (storageAction)
        {
            (await red.Items.SingleAsync(x => x.Id == repositoryIds[id], TestContext.Current.CancellationToken))
                .TenantId.ShouldBe("red");
        }

        // Exercise a second tenant in the same host, rather than only checking a query filter.
        var blueId = Guid.NewGuid();
        await host.ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("blue", command(blueId)));
        (await blue.Items.SingleAsync(x => x.Id == blueId, TestContext.Current.CancellationToken)).TenantId.ShouldBe("blue");
        (await blue.Projections.SingleAsync(x => x.Id == blueId, TestContext.Current.CancellationToken)).TenantId.ShouldBe("blue");
        (await red.Projections.AnyAsync(x => x.Id == blueId, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await red.Items.AnyAsync(x => x.Id == blueId, TestContext.Current.CancellationToken)).ShouldBeFalse();

        var bus = host.MessageBus();
        bus.TenantId = "red";
        var failedId = Guid.NewGuid();
        await Should.ThrowAsync<InvalidOperationException>(() => bus.InvokeAsync(command(failedId, true)));
        (await red.Items.AnyAsync(x => x.Id == failedId, TestContext.Current.CancellationToken)).ShouldBeFalse();

        if (storageAction)
        {
            (await red.Items.AnyAsync(x => x.Id == repositoryIds[failedId], TestContext.Current.CancellationToken)).ShouldBeFalse();
        }

        // A durable inbox failure must abort the source write, not just suppress its projection.
        await red.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION abstract_tenant_messages.reject_envelope() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Synthetic inbox failure'; END; $$;
            CREATE TRIGGER reject_envelope BEFORE INSERT ON abstract_tenant_messages.wolverine_incoming_envelopes
            FOR EACH ROW EXECUTE FUNCTION abstract_tenant_messages.reject_envelope();
            """, TestContext.Current.CancellationToken);
        var rejectedId = Guid.NewGuid();
        try
        {
            var exception = await Should.ThrowAsync<DbUpdateException>(() => bus.InvokeAsync(command(rejectedId)));
            exception.InnerException.ShouldBeOfType<PostgresException>().MessageText.ShouldBe("Synthetic inbox failure");
            (await red.Items.AnyAsync(x => x.Id == rejectedId, TestContext.Current.CancellationToken)).ShouldBeFalse();
            (await red.Projections.AnyAsync(x => x.Id == rejectedId, TestContext.Current.CancellationToken)).ShouldBeFalse();
            if (storageAction)
            {
                (await red.Items.AnyAsync(x => x.Id == repositoryIds[rejectedId], TestContext.Current.CancellationToken)).ShouldBeFalse();
            }
        }
        finally
        {
            await red.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER reject_envelope ON abstract_tenant_messages.wolverine_incoming_envelopes;
                DROP FUNCTION abstract_tenant_messages.reject_envelope();
                """, TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}

public enum HandlerShape { WithContext, RepositoryOnly, StorageAction }

public interface IItemRepository
{
    void Add(Item item);
}

public interface IItemQueries;

public class Item : ITenanted
{
    public Guid Id { get; set; }
    public string? TenantId { get; set; }
    private readonly List<ItemCreated> _events = [];
    public void RecordCreation() => _events.Add(new ItemCreated(Id));
    public ItemCreated[] DequeueEvents()
    {
        var events = _events.ToArray();
        _events.Clear();
        return events;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("abstract_tenant_data");
    }
}

public record CreateItem(Guid Id, bool Fail = false, bool AutomaticEvents = false);
public record ItemCreated(Guid Id);
public record CreateViaRepository(Guid Id, bool Fail = false, bool AutomaticEvents = false);

// The exact abstraction-only shape: no concrete context parameter or storage-action return type.
[WolverineIgnore]
public class CreateViaRepositoryHandler(IItemRepository repository)
{
    public ItemCreated? Handle(CreateViaRepository command)
    {
        var item = new Item { Id = command.Id };
        if (command.AutomaticEvents) item.RecordCreation();
        repository.Add(item);
        if (command.Fail) throw new InvalidOperationException("Synthetic handler failure");
        return command.AutomaticEvents ? null : new ItemCreated(command.Id);
    }
}
public record StoreItem(Guid Id, Guid RepositoryId, bool Fail = false, bool AutomaticEvents = false);

// No concrete context in the handler signature: the repository must still be tenant-built.
// Returning Insert exercises the entity overload of ApplyTransactionSupport.
[WolverineIgnore]
public class StoreItemHandler(IItemRepository repository)
{
    public (Insert<Item>, ItemCreated?) Handle(StoreItem command)
    {
        // Write through both the abstraction and the storage action in the same unit of work.
        repository.Add(new Item { Id = command.RepositoryId });
        if (command.Fail) throw new InvalidOperationException("Synthetic handler failure");
        var item = new Item { Id = command.Id };
        if (command.AutomaticEvents) item.RecordCreation();
        return (new Insert<Item>(item), command.AutomaticEvents ? null : new ItemCreated(command.Id));
    }
}

[WolverineIgnore]
public class CreateItemHandler(IItemRepository repository)
{
    public ItemCreated? Handle(CreateItem command, IItemQueries queries, Context db)
    {
        ReferenceEquals(repository, db).ShouldBeTrue();
        ReferenceEquals(queries, db).ShouldBeTrue();
        var item = new Item { Id = command.Id };
        if (command.AutomaticEvents) item.RecordCreation();
        repository.Add(item);
        if (command.Fail) throw new InvalidOperationException("Synthetic handler failure");
        return command.AutomaticEvents ? null : new ItemCreated(command.Id);
    }
}

[WolverineIgnore]
public class ItemCreatedHandler
{
    public static void Handle(ItemCreated message, Context db) => db.Projections.Add(new Projection { Id = message.Id });
}
