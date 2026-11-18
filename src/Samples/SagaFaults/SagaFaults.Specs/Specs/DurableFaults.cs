using Bobcat;
using SagaFaults.Inventory;
using SagaFaults.Orders;
using SagaFaults.Orders.FaultHandling;
using Wolverine;

namespace SagaFaults.Specs;

// GH-5554, chapter 1 continued: on a durable endpoint whose dead letters go to the message store, the Fault<T> is written
// to the outbox in the same transaction as the dead-letter move, so a crash between the two can no longer lose it. Where
// the fault goes is worked out before that transaction: the address the sender asked for (GH-5553's
// wolverine.fault.reply-to header, stamped here by the Order saga) and, with fault events on, the routes.
// Inline and buffered endpoints, and dead letters kept by the broker, stay best-effort: that is in the docs, not here.

[BobcatFeature("A fault is written with its dead letter")]
[Collection(nameof(DurableFaultCollection))]
public class a_fault_is_written_with_its_dead_letter(DurableFaultServices services) : SagaFaultsSpec(services)
{
    [BobcatSpec(typeof(PlaceOrder))]
    public async Task a_fault_survives_a_crash_right_after_the_dead_letter_move()
    {
        await InventoryHas(0, "widget");
        GivenTheServiceCrashesAfterItsNextDeadLetter(InventoryService.Name);

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenDeadLettered<ReserveInventory>(by: InventoryService.Name);
        await ThenWaitingInTheOutbox<Fault<ReserveInventory>>(by: InventoryService.Name, to: TwoServices.OrdersName);
        ThenNotHandled<Fault<ReserveInventory>>(by: TwoServices.OrdersName);

        await WhenTheServiceRestarts(InventoryService.Name);

        ThenTheSagaHandled<Order, Fault<ReserveInventory>>(OrderId);
        await ThenNothingWaitingInTheOutbox<Fault<ReserveInventory>>(by: InventoryService.Name);
    }

    [BobcatSpec(typeof(PlaceOrder))]
    public async Task a_dead_letter_move_that_rolls_back_takes_the_fault_with_it()
    {
        await InventoryHas(0, "widget");
        GivenTheNextDeadLetterMoveFails(InventoryService.Name);

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenNotDeadLettered<ReserveInventory>(by: InventoryService.Name);
        await ThenStillInTheInbox<ReserveInventory>(by: InventoryService.Name);
        await ThenNothingWaitingInTheOutbox<Fault<ReserveInventory>>(by: InventoryService.Name);
        ThenNotHandled<Fault<ReserveInventory>>(by: TwoServices.OrdersName);
    }
}

[BobcatFeature("With fault events on, every fault is written with its dead letter")]
[Collection(nameof(DurableFaultEventCollection))]
public class every_fault_is_written_with_its_dead_letter(DurableFaultEventServices services) : SagaFaultsSpec(services)
{
    // Inventory publishes fault events here, and routes its own Fault<ReserveInventory> to a durable local audit queue.
    // The saga asked for the fault too, so the one dead letter has two faults, and both are written in its transaction
    [BobcatSpec(typeof(PlaceOrder))]
    public async Task both_the_saga_and_the_audit_queue_get_their_fault_after_a_crash()
    {
        await InventoryHas(0, "widget");
        GivenTheServiceCrashesAfterItsNextDeadLetter(InventoryService.Name);

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenDeadLettered<ReserveInventory>(by: InventoryService.Name);
        ThenNotHandled<Fault<ReserveInventory>>(by: TwoServices.OrdersName);
        ThenNotHandled<Fault<ReserveInventory>>(by: InventoryService.Name);

        await WhenTheServiceRestarts(InventoryService.Name);

        ThenTheSagaHandled<Order, Fault<ReserveInventory>>(OrderId);
        ThenHandled<Fault<ReserveInventory>>(by: InventoryService.Name);
    }
}
