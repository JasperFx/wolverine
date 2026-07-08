using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests.Bugs;

// GH-3342: a saga whose Start returns Storage.Insert<T> of a NON-saga entity, then cascades a command whose
// handler loads that same entity via [Entity], regressed at 6.5.0. The [Entity] load came back null (so a
// Required=false handler NRE'd on it, and a Required handler was silently skipped) because the storage
// action written by an earlier handler in the flow was not persisted before the cascaded message was
// handled. Reproduces 6.4.3-vs-6.5.0 without Kafka (transports are local) and without OnException.Discard
// so the real failure surfaces.
public class Bug_3342_saga_entity_and_storage_action : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery().IncludeType<OrderSaga3342>();

                opts.Services.AddDbContextWithWolverineIntegration<Order3342DbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishAllMessages().Locally();
            }).StartAsync();

        await _host.ResetResourceState();

        // Seed the source Order the saga reads from.
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Order3342DbContext>();
        db.Orders.Add(new Order3342 { Id = TheOrderId, StockCheckNeeded = true });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static readonly Guid TheOrderId = Guid.NewGuid();

    [Fact]
    public async Task cascaded_handler_sees_the_persisted_entity()
    {
        var session = await _host
            .TrackActivity()
            .Timeout(30.Seconds())
            .InvokeMessageAndWaitAsync(new OrderCheckedOut3342(TheOrderId));

        // The Handles(ProcessOrder) chain must cascade OrderProcessed, which requires it to have loaded the
        // OrderProcessRecord that Start inserted.
        session.Sent.AllMessages().OfType<OrderProcessed3342>().Any(x => x.Id == TheOrderId)
            .ShouldBeTrue("the ProcessOrder handler must load the inserted record and cascade OrderProcessed");

        // And the record persisted by the Update in the ProcessOrder handler must be up to date.
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Order3342DbContext>();
        var record = await db.OrderProcessRecords.FirstOrDefaultAsync(x => x.Id == TheOrderId);
        record.ShouldNotBeNull("Start's Storage.Insert must be persisted");
        record.StockChecked.ShouldBeTrue("the ProcessOrder handler's Storage.Update must be persisted");
    }
}

public record OrderCheckedOut3342(Guid Id);

public record ProcessOrder3342(Guid Id);

public record OrderProcessed3342(Guid Id);

// [WolverineIgnore] keeps conventional discovery in OTHER tests in this assembly from picking up this saga
// (and failing on its unmapped storage action); the test's own host forces it in via IncludeType.
[WolverineIgnore]
public class OrderSaga3342 : Saga
{
    [SagaIdentity] public Guid Id { get; set; }

    public static (Insert<OrderProcessRecord3342>, ProcessOrder3342, OrderSaga3342) Start(OrderCheckedOut3342 message)
    {
        return
        (
            Storage.Insert(new OrderProcessRecord3342 { Id = message.Id, StockChecked = false }),
            new ProcessOrder3342(message.Id),
            new OrderSaga3342 { Id = message.Id }
        );
    }

    public (IStorageAction<OrderProcessRecord3342>, OrderProcessed3342) Handles(
        ProcessOrder3342 command,
        [Entity(Required = false)] OrderProcessRecord3342 orderProcessRecord,
        ILogger<OrderSaga3342> logger,
        Order3342DbContext db)
    {
        var order = db.Orders.First(o => o.Id == command.Id);
        orderProcessRecord.StockChecked = order.StockCheckNeeded;

        return
        (
            Storage.Update(orderProcessRecord),
            new OrderProcessed3342(command.Id)
        );
    }

    public void Handles(OrderProcessed3342 message, [Entity] OrderProcessRecord3342 orderProcessRecord)
    {
        // The required [Entity] load here was returning null in 6.5.0+, so this handler was silently skipped.
        if (!orderProcessRecord.StockChecked)
        {
            MarkCompleted();
        }
    }
}

public class Order3342
{
    public Guid Id { get; set; }
    public bool StockCheckNeeded { get; set; }
}

public class OrderProcessRecord3342
{
    public Guid Id { get; set; }
    public bool StockChecked { get; set; }
}

public class Order3342DbContext : DbContext
{
    public Order3342DbContext(DbContextOptions<Order3342DbContext> options) : base(options)
    {
    }

    public DbSet<Order3342> Orders { get; set; }
    public DbSet<OrderProcessRecord3342> OrderProcessRecords { get; set; }

    // NOTE: the OrderSaga3342 is deliberately NOT mapped here — it is persisted by the SqlServer message
    // store, exactly like the reporter's setup. Only the storage-action entity uses EF Core. That mix is
    // what #3040's `if (chain is SagaChain) return;` broke.

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order3342>(map =>
        {
            map.ToTable("orders_3342", "bug3342");
            map.HasKey(x => x.Id);
        });

        modelBuilder.Entity<OrderProcessRecord3342>(map =>
        {
            map.ToTable("order_process_records_3342", "bug3342");
            map.HasKey(x => x.Id);
        });
    }
}
