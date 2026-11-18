using Bobcat;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SagaFaults.Inventory;
using SagaFaults.Orders;
using Wolverine.Bobcat;

namespace SagaFaults.Specs;

/// <summary>
/// The base of every specification here: acts in the orders service, also tracks the inventory service, and treats
/// a dead letter as an outcome to judge rather than a failed act.
/// </summary>
public abstract class SagaFaultsSpec : WolverineSpec, IAsyncLifetime
{
    private readonly TwoServices _services;

    protected SagaFaultsSpec(TwoServices services) : base(services.Orders)
    {
        _services = services;

        AlsoTrack(services.Inventory);
        DeadLettersFailTheAct = false;

        // Three attempts in inventory, a hop over TCP and back, a saga load in orders
        Timeout = TimeSpan.FromSeconds(30);
    }

    protected TwoServices TheServices => _services;

    /// <summary>Every specification is about one order.</summary>
    protected Guid OrderId { get; } = Guid.NewGuid();

    public async ValueTask InitializeAsync() => await _services.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- the inventory service's state --------------------------------------------------------

    [Given("inventory has {available} of {sku} in stock")]
    public Task InventoryHas(int available, string sku)
        => storeInInventory(new StockItem { Id = sku, Available = available });

    [Given("{sku} is backordered")]
    public Task Backordered(string sku) => storeInInventory(new StockItem { Id = sku, Backordered = true });

    [Given("{sku} is discontinued")]
    public Task Discontinued(string sku) => storeInInventory(new StockItem { Id = sku, Discontinued = true });

    [Given("the warehouse is offline")]
    public Task WarehouseIsOffline() => storeInInventory(new Warehouse { Offline = true });

    // ---- the orders service's state -----------------------------------------------------------

    [Given("the ledger is unavailable")]
    public void LedgerIsUnavailable() => _services.Orders.Services.GetRequiredService<Ledger>().Available = false;

    private async Task storeInInventory(object document)
    {
        await using var session = _services.Inventory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(document);
        await session.SaveChangesAsync();
    }
}
