using Bobcat;
using Marten;
using Microsoft.Extensions.DependencyInjection;
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

// GH-5699: the saga and the step that fails are in the same service, and every local queue there is durable. The
// Fault<ChargeCard> goes to the saga's durable local queue, so it is written to the INBOX in the dead letter's
// transaction, not the outbox. A crash between that commit and the hand-off to the local queue leaves it there, and the
// restart has to recover it from the inbox.
[BobcatFeature("A fault to a durable local queue is written with its dead letter")]
[Collection(nameof(DurableLocalFaultCollection))]
public class a_fault_to_a_durable_local_queue_is_written_with_its_dead_letter(DurableLocalFaultServices services)
    : SagaFaultsSpec(services)
{
    [BobcatSpec(typeof(PlaceOrder))]
    public async Task the_saga_gets_its_fault_after_the_restart_step()
    {
        await InventoryHas(5, "widget");
        GivenTheServiceCrashesAfterItsNextDeadLetter(TwoServices.OrdersName);

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 5000m));

        ThenDeadLettered<ChargeCard>(by: TwoServices.OrdersName);
        ThenNotHandled<Fault<ChargeCard>>(by: TwoServices.OrdersName);

        await WhenTheServiceRestarts(TwoServices.OrdersName);

        ThenTheSagaHandled<Order, Fault<ChargeCard>>(OrderId);
        await ThenSaga<Order>(OrderId, Specify<Order>().With(x => x.FailedStep, nameof(ChargeCard)));
    }

    // The same crash, then a real restart: the orders host is stopped and disposed, and a new one is started against the
    // same database. The new host only has what the old one left in its inbox
    [BobcatSpec(typeof(PlaceOrder))]
    public async Task the_saga_gets_its_fault_after_a_real_restart()
    {
        await InventoryHas(5, "widget");
        GivenTheServiceCrashesAfterItsNextDeadLetter(TwoServices.OrdersName);

        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 5000m));

        ThenDeadLettered<ChargeCard>(by: TwoServices.OrdersName);
        ThenNotHandled<Fault<ChargeCard>>(by: TwoServices.OrdersName);

        await OrdersIsStoppedAndStartedAgain();

        await ThenTheRestartedOrderHeardThatAStepFailed(OrderId, nameof(ChargeCard));
    }

    [When("orders is stopped, and a new orders host is started against the same database")]
    public Task OrdersIsStoppedAndStartedAgain() => TheServices.RestartOrdersAsync();

    /// <summary>
    /// Read from the restarted orders service, whose saga store this specification's own host no longer is
    /// </summary>
    [Then("the restarted orders service's Order saga {orderId} hears that {failedStep} failed")]
    public async Task ThenTheRestartedOrderHeardThatAStepFailed(Guid orderId, string failedStep)
    {
        var store = TheServices.Orders.Services.GetRequiredService<IDocumentStore>();
        var deadline = DateTimeOffset.UtcNow.Add(Timeout);
        Order? order;

        do
        {
            await using var session = store.QuerySession();
            order = await session.LoadAsync<Order>(orderId);
            if (order?.FailedStep != null) break;

            await Task.Delay(100);
        } while (DateTimeOffset.UtcNow < deadline);

        SpecAssert.Check("failed step", order?.FailedStep ?? "(none yet)", failedStep);
    }
}
