using IntegrationTests;
using JasperFx.Core;
using Wolverine.ErrorHandling;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SagaFaults.Inventory;
using SagaFaults.Orders;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace SagaFaults.Specs;

/// <summary>
/// Two Wolverine services in one test process, talking over TCP: "orders", where the Order saga lives, and
/// "inventory". Each has its own Marten store (a schema of its own) and its own durable inbox and outbox.
/// </summary>
public abstract class TwoServices : IAsyncLifetime
{
    public const string OrdersName = "orders";

    public IHost Orders { get; private set; } = null!;
    public IHost Inventory { get; private set; } = null!;

    public int OrdersPort { get; } = PortFinder.GetAvailablePort();
    public int InventoryPort { get; } = PortFinder.GetAvailablePort();

    public Uri OrdersUri => new($"tcp://localhost:{OrdersPort}");

    /// <summary>A schema prefix of the fixture's own, so the fixtures' sagas and dead letters never meet.</summary>
    protected abstract string Schema { get; }

    /// <summary>The Order saga for this chapter, and anything else only this fixture's orders service has.</summary>
    protected abstract void ConfigureOrders(WolverineOptions opts);

    /// <summary>Anything only this fixture's inventory service has. Nothing, unless a fixture says otherwise.</summary>
    protected virtual void ConfigureInventory(WolverineOptions opts)
    {
    }

    public async ValueTask InitializeAsync()
    {
        Inventory = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                InventoryService.Configure(opts, InventoryPort, OrdersPort, $"{Schema}_inventory");
                ConfigureInventory(opts);
            })
            .StartAsync();

        Orders = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = OrdersName;

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = $"{Schema}_orders";
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
                opts.Services.AddSingleton<Ledger>();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(CardPayments))
                    .IncludeType(typeof(StockChecks))
                    .IncludeType(typeof(Storefront));

                opts.ListenAtPort(OrdersPort).UseDurableInbox();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                opts.PublishMessage<ReserveInventory>().ToPort(InventoryPort);
                opts.PublishMessage<CheckStock>().ToPort(InventoryPort);

                // As in inventory: retried twice, then dead-lettered. Fault events are off here too
                opts.OnException<Exception>().RetryTimes(2);

                ConfigureOrders(opts);
            })
            .StartAsync();

        await ResetAsync();
    }

    /// <summary>Every specification starts from empty stores and a working ledger.</summary>
    public async Task ResetAsync()
    {
        await Orders.ResetAllMartenDataAsync();
        await Inventory.ResetAllMartenDataAsync();
        Orders.Services.GetRequiredService<Ledger>().Available = true;
    }

    public async ValueTask DisposeAsync()
    {
        await Orders.StopAsync();
        await Inventory.StopAsync();
        Orders.Dispose();
        Inventory.Dispose();
    }
}

/// <summary>Chapter 1 (GH-5553, GH-4484): the Order saga that handles Fault&lt;T&gt; itself.</summary>
public class FaultHandlingServices : TwoServices
{
    protected override string Schema => "saga_faults";

    protected override void ConfigureOrders(WolverineOptions opts)
    {
        opts.Discovery.IncludeType(typeof(Orders.FaultHandling.Order));

        // GH-4484: messages for a saga are grouped by its id, read from the saga-id header when the message has no
        // saga id member
        opts.MessagePartitioning.UseInferredMessageGrouping();
    }
}

/// <summary>GH-5554: the chapter 1 saga again, on hosts of its own, because these specifications stop and restart a service.</summary>
public class DurableFaultServices : FaultHandlingServices
{
    protected override string Schema => "saga_durable_faults";
}

/// <summary>
/// GH-5554 with fault events on in inventory: its own Fault&lt;ReserveInventory&gt; route to the durable "fault-audit"
/// queue is used as well as the address the saga asked for, so one dead letter has two faults to write.
/// </summary>
public class DurableFaultEventServices : FaultHandlingServices
{
    protected override string Schema => "saga_durable_fault_events";

    protected override void ConfigureInventory(WolverineOptions opts)
    {
        opts.PublishFaultEvents();
        opts.LocalQueue("fault-audit").UseDurableInbox();
    }
}

/// <summary>Chapter 2 (GH-5556): the Order saga that compensates.</summary>
public class CompensationServices : TwoServices
{
    protected override string Schema => "saga_compensation";

    protected override void ConfigureOrders(WolverineOptions opts)
        => opts.Discovery.IncludeType(typeof(Orders.Compensation.Order));
}

[CollectionDefinition(nameof(FaultHandlingCollection))]
public class FaultHandlingCollection : ICollectionFixture<FaultHandlingServices>;

[CollectionDefinition(nameof(DurableFaultCollection))]
public class DurableFaultCollection : ICollectionFixture<DurableFaultServices>;

[CollectionDefinition(nameof(DurableFaultEventCollection))]
public class DurableFaultEventCollection : ICollectionFixture<DurableFaultEventServices>;

[CollectionDefinition(nameof(CompensationCollection))]
public class CompensationCollection : ICollectionFixture<CompensationServices>;
