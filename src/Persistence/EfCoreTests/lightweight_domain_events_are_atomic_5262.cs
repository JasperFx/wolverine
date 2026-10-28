using EfCoreTests.ManagedTenancyAbstractions;
using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace EfCoreTests.LightweightDomainEvents;

// GH-5262. A Lightweight message handler saved its entity changes, scraped the domain events, then saved
// again for the events' envelopes -- so when that second save failed, the entity was already committed and
// the event was gone. The scrape now happens before the one save, which covers both.
//
// managed_tenancy_abstraction_tests covers the same thing for a tenanted DbContext. This is the plain one.
public class lightweight_domain_events_are_atomic_5262
{
    [Theory]
    [InlineData(TransactionMiddlewareMode.Eager)]
    [InlineData(TransactionMiddlewareMode.Lightweight)]
    public async Task a_scraped_domain_event_is_published(TransactionMiddlewareMode mode)
    {
        using var host = await startHostAsync(mode);
        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new CreateOrder(id));

        await using var db = openDatabase(host);
        (await db.Orders.AnyAsync(x => x.Id == id, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await db.Receipts.AnyAsync(x => x.Id == id, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(TransactionMiddlewareMode.Eager)]
    [InlineData(TransactionMiddlewareMode.Lightweight)]
    public async Task a_failed_event_envelope_write_rolls_back_the_entity(TransactionMiddlewareMode mode)
    {
        using var host = await startHostAsync(mode);
        var id = Guid.NewGuid();

        await using var db = openDatabase(host);

        await using (await InboxInsertsRejected.StartAsync(db, MessageSchema))
        {
            await Should.ThrowAsync<DbUpdateException>(() => host.MessageBus().InvokeAsync(new CreateOrder(id)));
        }

        (await db.Orders.AnyAsync(x => x.Id == id, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private const string MessageSchema = "lightweight_events_messages";

    private static Task<IHost> startHostAsync(TransactionMiddlewareMode mode)
    {
        return Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType<CreateOrderHandler>()
                .IncludeType<OrderPlacedHandler>();

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, MessageSchema);
            opts.Services.AddDbContextWithWolverineIntegration<OrderContext>(x =>
                x.UseNpgsql(Servers.PostgresConnectionString));

            opts.UseEntityFrameworkCoreTransactions(mode);
            opts.PublishDomainEventsFromEntityFrameworkCore<Order, OrderPlaced>(order => order.DequeueEvents());

            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();
            opts.PublishMessage<OrderPlaced>().ToLocalQueue("lightweight-events-receipts");

            opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            opts.Services.AddResourceSetupOnStartup();
        }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static OrderContext openDatabase(IHost host)
    {
        var options = new DbContextOptionsBuilder<OrderContext>()
            .UseNpgsql(Servers.PostgresConnectionString)
            .Options;

        return new OrderContext(options);
    }
}

public class Order
{
    private readonly List<OrderPlaced> _events = [];

    public Guid Id { get; set; }

    public void Place() => _events.Add(new OrderPlaced(Id));

    public OrderPlaced[] DequeueEvents()
    {
        var events = _events.ToArray();
        _events.Clear();
        return events;
    }
}

public class Receipt
{
    public Guid Id { get; set; }
}

public class OrderContext(DbContextOptions<OrderContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Receipt> Receipts => Set<Receipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("lightweight_events_data");
    }
}

public record CreateOrder(Guid Id);

public record OrderPlaced(Guid Id);

[WolverineIgnore]
public class CreateOrderHandler
{
    // No returned message: OrderPlaced only goes out through the domain event scraper
    public static void Handle(CreateOrder command, OrderContext db)
    {
        var order = new Order { Id = command.Id };
        order.Place();
        db.Orders.Add(order);
    }
}

[WolverineIgnore]
public class OrderPlacedHandler
{
    public static void Handle(OrderPlaced message, OrderContext db) => db.Receipts.Add(new Receipt { Id = message.Id });
}
