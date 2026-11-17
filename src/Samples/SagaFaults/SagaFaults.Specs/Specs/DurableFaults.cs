using Bobcat;
using SagaFaults.Inventory;
using SagaFaults.Orders;
using SagaFaults.Orders.FaultHandling;
using Wolverine;

namespace SagaFaults.Specs;

// GH-5554: on a durable endpoint whose dead letters go to the message store, the Fault<T> is written to the outbox in
// the same transaction as the dead-letter move, so a crash between the two can no longer lose it.

[BobcatFeature("A fault is written with its dead letter")]
[Collection(nameof(DurableFaultCollection))]
public class a_fault_is_written_with_its_dead_letter(DurableFaultServices services) : SagaFaultsSpec(services)
{
    [BobcatSpec(typeof(PlaceOrder), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_fault_survives_a_crash_right_after_the_dead_letter_move()
    {
        await InventoryHas(0, "widget");
        GivenTheServiceDiesAfterItsNextDeadLetter(InventoryService.Name);

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenDeadLettered<ReserveInventory>(by: InventoryService.Name);
        ThenNotHandledBy<Fault<ReserveInventory>>(TwoServices.OrdersName);

        await WhenTheServiceRecovers(InventoryService.Name);

        ThenTheSagaHandled<Order, Fault<ReserveInventory>>(OrderId);
    }

    [BobcatSpec(typeof(PlaceOrder), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_dead_letter_move_that_rolls_back_takes_the_fault_with_it()
    {
        await InventoryHas(0, "widget");
        GivenTheNextDeadLetterMoveFails(InventoryService.Name);

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenNotDeadLettered<ReserveInventory>(by: InventoryService.Name);
        ThenNotHandledBy<Fault<ReserveInventory>>(TwoServices.OrdersName);
    }
}
