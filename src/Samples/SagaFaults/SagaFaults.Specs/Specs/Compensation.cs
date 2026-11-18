using Bobcat;
using SagaFaults.Inventory;
using SagaFaults.Orders;
using SagaFaults.Orders.Compensation;
using Wolverine;

namespace SagaFaults.Specs;

// GH-5556, chapter 2 of the saga example: the Order saga compensates a failed step, selected by the failed message and
// the exception type, after the retries are used up. Its status and compensation log come from the CompensatingSaga base type.

[BobcatFeature("A saga compensates a step that failed in another service")]
[Collection(nameof(CompensationCollection))]
public class compensating_a_step_that_failed_downstream(CompensationServices services) : SagaFaultsSpec(services)
{
    [BobcatSpec(typeof(PlaceOrder))]
    public async Task an_item_out_of_stock_cancels_the_order_and_tells_the_customer()
    {
        await InventoryHas(0, "widget");

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenDeadLettered<ReserveInventory>(by: InventoryService.Name);
        await ThenTheSagaCompensated<Order, ReserveInventory, OutOfStockException>(OrderId);
        ThenMessageSent(new CancelOrder(OrderId));
        ThenMessageSent(new NotifyCustomer(OrderId, "widget is out of stock"));
        ThenNoMessageSent<RefundPayment>();
        await ThenTheSagaStatusIs<Order>(OrderId, SagaStatus.Compensated);
    }

    // GH-5555: the fault carries the exception's type hierarchy, so a subclass thrown in another service still matches
    [BobcatSpec(typeof(PlaceOrder))]
    public async Task a_backordered_item_is_compensated_as_out_of_stock()
    {
        await Backordered("widget");

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenDeadLettered<ReserveInventory>(by: InventoryService.Name);
        await ThenTheSagaCompensated<Order, ReserveInventory, BackorderedException>(OrderId);
        ThenMessageSent(new NotifyCustomer(OrderId, "widget is out of stock"));
        ThenNoMessageSent<RefundPayment>();
    }

    [BobcatSpec(typeof(PlaceOrder))]
    public async Task any_other_failure_of_the_reservation_refunds_the_payment()
    {
        await WarehouseIsOffline();

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenDeadLettered<ReserveInventory>(by: InventoryService.Name);
        await ThenTheSagaCompensated<Order, ReserveInventory, WarehouseOfflineException>(OrderId);
        ThenMessageSent(new RefundPayment(OrderId, 100m));
        ThenNoMessageSent<NotifyCustomer>();
        await ThenTheSagaStatusIs<Order>(OrderId, SagaStatus.Compensated);
    }
}

[BobcatFeature("A saga compensates its own failed handler")]
[Collection(nameof(CompensationCollection))]
public class compensating_the_sagas_own_failed_handler(CompensationServices services) : SagaFaultsSpec(services)
{
    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_payment_the_ledger_cannot_record_is_retried_and_then_refunded()
    {
        await GivenSaga<Order>(AnOrderAwaitingPayment(100m));
        LedgerIsUnavailable();

        await WhenReceived(new PaymentReceived(OrderId, 100m));

        ThenAttempted<PaymentReceived>(3);
        await ThenDeadLettered<PaymentReceived>(by: TwoServices.OrdersName, tagged: "compensated");
        await ThenTheSagaCompensated<Order, PaymentReceived, LedgerUnavailableException>(OrderId);
        ThenMessageSent(new RefundPayment(OrderId, 100m));
        await ThenTheSagaStatusIs<Order>(OrderId, SagaStatus.Compensated);
    }
}

[BobcatFeature("A terminal failure is compensated without retrying")]
[Collection(nameof(CompensationCollection))]
public class compensating_a_terminal_failure(CompensationServices services) : SagaFaultsSpec(services)
{
    // PaymentAmountMismatchException is marked [TerminalFailure]
    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_wrong_payment_amount_is_refunded_at_once()
    {
        await GivenSaga<Order>(AnOrderAwaitingPayment(100m));

        await WhenReceived(new PaymentReceived(OrderId, 90m));

        ThenAttempted<PaymentReceived>(1);
        await ThenDeadLettered<PaymentReceived>(by: TwoServices.OrdersName, tagged: "compensated");
        await ThenTheSagaCompensated<Order, PaymentReceived, PaymentAmountMismatchException>(OrderId);
        ThenMessageSent(new RefundPayment(OrderId, 90m));
        await ThenTheSagaStatusIs<Order>(OrderId, SagaStatus.Compensated);
    }

    // DiscontinuedException implements ITerminalFailure, and is thrown in the inventory service, which has no saga
    [BobcatSpec(typeof(PlaceOrder))]
    public async Task a_discontinued_item_is_not_retried_in_the_other_service_either()
    {
        await Discontinued("widget");

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        ThenAttempted<ReserveInventory>(1, by: InventoryService.Name);
        ThenDeadLettered<ReserveInventory>(by: InventoryService.Name);
        await ThenTheSagaCompensated<Order, ReserveInventory, DiscontinuedException>(OrderId);
        ThenMessageSent(new RefundPayment(OrderId, 100m));
    }
}

[BobcatFeature("When compensation itself fails")]
[Collection(nameof(CompensationCollection))]
public class when_compensation_fails(CompensationServices services) : SagaFaultsSpec(services)
{
    // The Compensate for a wrong amount throws RefundAlreadyIssuedException when the order was already refunded.
    // Compensation runs as a handler chain of its own, so the orders service's retry policy applies to it first
    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_compensation_that_throws_leaves_the_saga_compensation_failed()
    {
        await GivenSaga<Order>(Specify<Order>()
            .With(x => x.Id, OrderId)
            .With(x => x.Amount, 100m)
            .With(x => x.Refunded, true));

        await WhenReceived(new PaymentReceived(OrderId, 90m));

        await ThenDeadLettered<PaymentReceived>(by: TwoServices.OrdersName, tagged: "compensation-failed");
        await ThenTheSagaFailedToCompensate<Order, PaymentReceived, PaymentAmountMismatchException>(OrderId);
        ThenNoMessageSent<RefundPayment>();
        await ThenTheSagaStatusIs<Order>(OrderId, SagaStatus.CompensationFailed);
    }

    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_concurrency_conflict_saving_the_compensated_saga_is_retried()
    {
        await GivenSaga<Order>(AnOrderAwaitingPayment(100m));
        WhileCompensatingTheSagaIsChanged<Order>(OrderId);

        await WhenReceived(new PaymentReceived(OrderId, 90m));

        await ThenDeadLettered<PaymentReceived>(by: TwoServices.OrdersName, tagged: "compensated");
        ThenMessageSent(new RefundPayment(OrderId, 90m));
        await ThenTheSagaStatusIs<Order>(OrderId, SagaStatus.Compensated);
    }

    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_conflict_on_every_attempt_leaves_the_saga_compensation_failed()
    {
        await GivenSaga<Order>(AnOrderAwaitingPayment(100m));
        WhileCompensatingTheSagaIsChanged<Order>(OrderId, onEveryAttempt: true);

        await WhenReceived(new PaymentReceived(OrderId, 90m));

        await ThenDeadLettered<PaymentReceived>(by: TwoServices.OrdersName, tagged: "compensation-failed");
        ThenNoMessageSent<RefundPayment>();
        await ThenTheSagaStatusIs<Order>(OrderId, SagaStatus.CompensationFailed);
    }
}

[BobcatFeature("When the saga is gone before it is compensated")]
[Collection(nameof(CompensationCollection))]
public class when_the_saga_is_gone(CompensationServices services) : SagaFaultsSpec(services)
{
    // Built with GH-5553: the saga asks for the fault because it declares NotFound(Fault<ReserveInventory>)
    [BobcatSpec(typeof(ReserveInventory))]
    public async Task a_fault_for_a_completed_order_goes_to_NotFound()
    {
        await InventoryHas(0, "widget");

        await WhenTracked("a ReserveInventory sent for an Order that has since completed fails in inventory",
            bus => bus.SendAsync(new ReserveInventory("widget", 1),
                new DeliveryOptions { SagaId = OrderId.ToString() }.ReturnFaultToSender()).AsTask());

        ThenDeadLettered<ReserveInventory>(by: InventoryService.Name);
        ThenMessageSent(new OrderAlreadyClosed("widget"));
        ThenNotDeadLettered<Fault<ReserveInventory>>(by: TwoServices.OrdersName);
    }

    // There is no NotFound for the saga's own failed handler (decided in the GH-5553 spec review, 2026-11-17)
    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_payment_for_an_order_completed_before_it_is_compensated_is_dead_lettered_as_saga_missing()
    {
        await GivenSaga<Order>(AnOrderAwaitingPayment(100m));
        WhileCompensatingTheSagaIsCompleted<Order>(OrderId);

        await WhenReceived(new PaymentReceived(OrderId, 90m));

        await ThenDeadLettered<PaymentReceived>(by: TwoServices.OrdersName, tagged: "compensation-saga-missing");
        ThenNoMessageSent<RefundPayment>();
        await ThenNoSaga<Order>(OrderId);
    }
}
