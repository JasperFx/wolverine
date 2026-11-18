using JasperFx.Core;
using Marten;
using Wolverine;
using IntegrationTests;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Transports.Tcp;

namespace SagaFaults.Inventory;

// The inventory service. It knows nothing about orders or sagas: it reserves stock, and it fails when it cannot.

// No saga id member: a ReserveInventory the Order saga sends is tied to the saga by the saga-id header alone
public record ReserveInventory(string Sku, int Quantity);

public record InventoryReserved(string Sku, int Quantity);

public record CheckStock(string Sku);

public record StockChecked(string Sku, int Available);

public class StockItem
{
    public string Id { get; set; } = null!; // the SKU
    public int Available { get; set; }
    public bool Backordered { get; set; }
    public bool Discontinued { get; set; }
}

public class Warehouse
{
    public const string Main = "main";
    public string Id { get; set; } = Main;
    public bool Offline { get; set; }
}

public class OutOfStockException(string sku) : Exception($"{sku} is out of stock")
{
    public string Sku { get; } = sku;
}

// A subclass of OutOfStockException: a saga compensating OutOfStockException compensates this too, across the wire
public class BackorderedException(string sku) : OutOfStockException(sku);

// Retrying cannot bring a discontinued item back: a terminal failure skips the retries (GH-5556)
public class DiscontinuedException(string sku) : Exception($"{sku} is discontinued"), ITerminalFailure;

public class WarehouseOfflineException() : Exception("The warehouse is offline");

public static class ReserveInventoryHandler
{
    public static async Task<InventoryReserved> Handle(ReserveInventory command, IDocumentSession session)
    {
        var item = await loadAvailableAsync(command.Sku, command.Quantity, session);
        item.Available -= command.Quantity;
        session.Store(item);

        return new InventoryReserved(command.Sku, command.Quantity);
    }

    internal static async Task<StockItem> loadAvailableAsync(string sku, int quantity, IQuerySession session)
    {
        var warehouse = await session.LoadAsync<Warehouse>(Warehouse.Main);
        if (warehouse is { Offline: true }) throw new WarehouseOfflineException();

        var item = await session.LoadAsync<StockItem>(sku);
        if (item is { Discontinued: true }) throw new DiscontinuedException(sku);
        if (item is { Backordered: true }) throw new BackorderedException(sku);
        if (item is null || item.Available < quantity) throw new OutOfStockException(sku);

        return item;
    }
}

public static class CheckStockHandler
{
    public static async Task<StockChecked> Handle(CheckStock command, IQuerySession session)
    {
        var item = await ReserveInventoryHandler.loadAvailableAsync(command.Sku, 1, session);
        return new StockChecked(command.Sku, item.Available);
    }
}

// The inventory service's own audit of its faults. Fault events are off in this service, so today nothing reaches it;
// GH-5553 says a fault the sender asked for goes only to the sender, so nothing should reach it then either
public static class FaultAudit
{
    public static void Handle(Fault<ReserveInventory> fault)
    {
    }
}

public static class InventoryService
{
    public const string Name = "inventory";

    public static void Configure(WolverineOptions opts, int port, int ordersPort, string schema)
    {
        opts.ServiceName = Name;

        opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = schema;
                m.DisableNpgsqlLogging = true;
            })
            .IntegrateWithWolverine();

        opts.Policies.AutoApplyTransactions();

        opts.Discovery.DisableConventionalDiscovery()
            .IncludeType(typeof(ReserveInventoryHandler))
            .IncludeType(typeof(CheckStockHandler))
            .IncludeType(typeof(FaultAudit));

        // Durable: a dead letter goes to this service's message store, which is where a durable fault is written (GH-5554)
        opts.ListenAtPort(port).UseDurableInbox();
        opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

        opts.PublishMessage<InventoryReserved>().ToPort(ordersPort);
        opts.PublishMessage<StockChecked>().ToPort(ordersPort);
        opts.PublishMessage<Fault<ReserveInventory>>().ToLocalQueue("fault-audit");

        // Every failure is retried twice, so three attempts, then dead-lettered. Fault events stay OFF: no
        // PublishFaultEvents() here, and no routing rule sends a fault back to orders
        opts.OnException<Exception>().RetryTimes(2);
    }
}
