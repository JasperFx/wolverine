using Bobcat;
using SagaFaults.Inventory;
using SagaFaults.Orders;
using SagaFaults.Orders.FaultHandling;
using Wolverine;

namespace SagaFaults.Specs;

// GH-5553, chapter 1 of the saga example: a Fault<T> goes back to the sender or saga that sent T, opt-in per message,
// before falling back to routing. Neither service turns on PublishFaultEvents(), and neither has a routing rule that
// sends a fault to the other.

[BobcatFeature("A failed step's fault goes back to the saga that sent it")]
[Collection(nameof(FaultHandlingCollection))]
public class a_failed_steps_fault_goes_back_to_the_saga(FaultHandlingServices services) : SagaFaultsSpec(services)
{
    [BobcatSpec(typeof(PlaceOrder), Lifecycle = Lifecycle.Acceptance)]
    public async Task the_saga_hears_that_a_step_failed_in_another_service()
    {
        await InventoryHas(0, "widget");

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenDeadLettered<ReserveInventory>(by: InventoryService.Name);
        ThenTheSagaHandled<Order, Fault<ReserveInventory>>(OrderId);
        await ThenSaga<Order>(OrderId, Specify<Order>()
            .With(x => x.FailedStep, nameof(ReserveInventory))
            .With(x => x.FailureReason, "widget is out of stock"));
    }

    [BobcatSpec(typeof(PlaceOrder), Lifecycle = Lifecycle.Acceptance)]
    public async Task the_fault_goes_only_to_the_saga_while_fault_events_are_off()
    {
        await InventoryHas(0, "widget");

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenDeadLettered<ReserveInventory>(by: InventoryService.Name);
        ThenHandledBy<Fault<ReserveInventory>>(TwoServices.OrdersName);

        // The inventory service routes its own Fault<ReserveInventory> to a local audit queue, but fault events are
        // off there, so that route is not used: the fault goes only where the saga asked for it
        ThenNotHandledBy<Fault<ReserveInventory>>(InventoryService.Name);
    }

    [BobcatSpec(typeof(PlaceOrder), Lifecycle = Lifecycle.Acceptance)]
    public async Task the_saga_hears_that_a_step_failed_in_its_own_service()
    {
        await InventoryHas(5, "widget");

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 5000m));

        ThenDeadLettered<ChargeCard>(by: TwoServices.OrdersName);
        ThenTheSagaHandled<Order, Fault<ChargeCard>>(OrderId);
        await ThenSaga<Order>(OrderId, Specify<Order>()
            .With(x => x.InventoryReserved, true)
            .With(x => x.FailedStep, nameof(ChargeCard)));
    }
}

[BobcatFeature("A sender asks for the fault of a message it sends")]
[Collection(nameof(FaultHandlingCollection))]
public class a_sender_asks_for_the_fault(FaultHandlingServices services) : SagaFaultsSpec(services)
{
    [BobcatSpec(typeof(CheckStock), Lifecycle = Lifecycle.Acceptance)]
    public async Task the_fault_goes_to_the_address_the_sender_named()
    {
        await InventoryHas(0, "widget");

        await WhenTracked("orders sends CheckStock(\"widget\") to inventory, asking for its fault at orders' address",
            bus => bus.SendAsync(new CheckStock("widget"), new DeliveryOptions().FaultTo(TheServices.OrdersUri)).AsTask());

        ThenDeadLettered<CheckStock>(by: InventoryService.Name);
        ThenHandledBy<Fault<CheckStock>>(TwoServices.OrdersName);
    }

    [BobcatSpec(typeof(CheckStock), Lifecycle = Lifecycle.Acceptance)]
    public async Task the_fault_goes_back_to_the_sender()
    {
        await InventoryHas(0, "widget");

        await WhenTracked("orders sends CheckStock(\"widget\") to inventory, asking for its fault back",
            bus => bus.SendAsync(new CheckStock("widget"), new DeliveryOptions().ReturnFaultToSender()).AsTask());

        ThenDeadLettered<CheckStock>(by: InventoryService.Name);
        ThenHandledBy<Fault<CheckStock>>(TwoServices.OrdersName);
    }

    // Today's behaviour, and it stays: a fault goes back only when the sender asks for it. With fault events off in
    // inventory there is no fault at all
    [BobcatSpec(typeof(CheckStock))]
    public async Task a_sender_that_does_not_ask_gets_no_fault()
    {
        await InventoryHas(0, "widget");

        await WhenReceived(new CheckStock("widget"));

        ThenDeadLettered<CheckStock>(by: InventoryService.Name);
        ThenNotHandledBy<Fault<CheckStock>>(TwoServices.OrdersName);
    }
}

// GH-4484, folded into GH-5553: a saga's identity from the saga-id header, Fault<T> included

[BobcatFeature("A saga's messages are tied to it by the saga-id header")]
[Collection(nameof(FaultHandlingCollection))]
public class saga_identity_from_the_saga_id_header(FaultHandlingServices services) : SagaFaultsSpec(services)
{
    [BobcatSpec(typeof(PlaceOrder), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_reply_with_no_saga_id_member_is_grouped_by_its_saga()
    {
        await InventoryHas(5, "widget");

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenGroupedBy<CardCharged>(OrderId);
        ThenTheSagaHandled<Order, CardCharged>(OrderId);
    }

    [BobcatSpec(typeof(PlaceOrder), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_fault_is_grouped_by_the_saga_that_sent_the_failed_message()
    {
        await InventoryHas(5, "widget");

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 5000m));

        ThenDeadLettered<ChargeCard>(by: TwoServices.OrdersName);
        ThenGroupedBy<Fault<ChargeCard>>(OrderId);
    }

    [BobcatSpec(typeof(ReserveInventory), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_fault_that_belongs_to_no_saga_goes_to_IdNotProvided()
    {
        await InventoryHas(0, "widget");

        await WhenTracked("orders reserves 1 widget outside any order, asking for the fault back",
            bus => bus.SendAsync(new ReserveInventory("widget", 1), new DeliveryOptions().ReturnFaultToSender()).AsTask());

        ThenDeadLettered<ReserveInventory>(by: InventoryService.Name);
        ThenHandledBy<Fault<ReserveInventory>>(TwoServices.OrdersName);
        ThenMessageSent(new UnmatchedInventoryFailure("widget"));
        ThenNotDeadLettered<Fault<ReserveInventory>>(by: TwoServices.OrdersName);
    }
}
