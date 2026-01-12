using IntegrationTests;
using Marten;
using Marten.Events;
using Marten.Events.Projections;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

public class mixed_aggregate_handler_with_multiple_streams
{
    [Fact]
    public async Task get_the_correct_aggregate_back_out()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                MartenServiceCollectionExtensions.AddMarten(opts.Services, m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "accounts";

                    m.Projections.Snapshot<XAccount>(SnapshotLifecycle.Inline);
                    m.Projections.Snapshot<Inventory>(SnapshotLifecycle.Inline);
                }).IntegrateWithWolverine();
            }).StartAsync();

        using var session = host.DocumentStore().LightweightSession();
        var inventoryId = session.Events.StartStream<Inventory>(new InventoryStarted("XFX", 100, 10)).Id;
        var accountId = session.Events.StartStream<XAccount>(new XAccountOpened(2000)).Id;
        await session.SaveChangesAsync();

        var (tracked, account) = await host.InvokeMessageAndWaitAsync<XAccount>(new MakePurchase(accountId, inventoryId, 30));
        account.Balance.ShouldBe(1700);
        
    }
}

public record XAccountOpened(double Balance);

public record ItemPurchased(Guid InventoryId, int Number, double UnitPrice);

public class XAccount
{
    public Guid Id { get; set; }
    public double Balance { get; set; }

    public XAccount()
    {
    }

    public static XAccount Create(XAccountOpened opened) => new XAccount { Balance = opened.Balance };

    public void Apply(ItemPurchased purchased)
    {
        Balance -= (purchased.Number * purchased.UnitPrice);
    }
}

public record InventoryStarted(string Name, int Quantity, double UnitPrice);

public record Drawdown(int Quantity);

public class Inventory
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public int Quantity { get; set; }
    public double UnitPrice { get; set; }

    public static Inventory Create(InventoryStarted started) => new Inventory
    {
        Name = started.Name,
        Quantity = started.Quantity,
        UnitPrice = started.UnitPrice
    };

    public void Apply(Drawdown down) => Quantity -= down.Quantity;
}

public record MakePurchase(Guid XAccountId, Guid InventoryId, int Number);

#region sample_MakePurchaseHandler

public static class MakePurchaseHandler
{
    // See how we used the generic version
    // of UpdatedAggregate to tell Wolverine we 
    // want *only* the XAccount as the response
    // from this handler
    public static UpdatedAggregate<XAccount> Handle(
        MakePurchase command,

        [WriteAggregate] IEventStream<XAccount> account,

        [WriteAggregate] IEventStream<Inventory> inventory)
    {
        if (command.Number > inventory.Aggregate.Quantity ||
            (command.Number * inventory.Aggregate.UnitPrice) > account.Aggregate.Balance)
        {
            // Do Nothing!
            return new UpdatedAggregate<XAccount>();
        }
        
        account.AppendOne(new ItemPurchased(command.InventoryId, command.Number, inventory.Aggregate.UnitPrice));
        inventory.AppendOne(new Drawdown(command.Number));
        
        return new UpdatedAggregate<XAccount>();
    }
}

#endregion