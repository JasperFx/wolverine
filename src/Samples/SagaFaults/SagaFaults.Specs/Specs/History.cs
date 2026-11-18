using Bobcat;
using SagaFaults.Inventory;
using SagaFaults.Orders;
using SagaFaults.Orders.Compensation;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace SagaFaults.Specs;

// GH-5665: a document saga that records its message history (here every saga, by opts.Policies.RecordSagaHistory()).
// The history is read through ISagaHistoryDiagnostics, as an event-sourced saga's stream is, one handled message to an
// entry: what it was, the outcome, and what the saga sent. A failed attempt is not an entry; it is a dead letter.

[BobcatFeature("A document saga records the messages it handled")]
[Collection(nameof(HistoryCollection))]
public class an_orders_message_history(HistoryServices services) : SagaFaultsSpec(services)
{
    [BobcatSpec(typeof(PlaceOrder))]
    public async Task the_history_holds_each_message_the_order_handled()
    {
        await InventoryHas(5, "widget");
        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        await WhenTheSagaHistoryIsRead<Order>(OrderId);

        ThenTheSagaHistoryIs(typeof(PlaceOrder), typeof(InventoryReserved));
        ThenTheSagaHistoryOutcomesAre(SagaHistoryOutcome.Started, SagaHistoryOutcome.Continued);
        await ThenTheSagaSupportsHistory<Order>();
    }

    [BobcatSpec(typeof(PlaceOrder))]
    public async Task a_compensated_step_is_in_the_history_as_its_fault()
    {
        await InventoryHas(0, "widget");
        await WhenReceived(new PlaceOrder(OrderId, "widget", 1, 100m));

        await WhenTheSagaHistoryIsRead<Order>(OrderId);

        ThenTheSagaHistoryIs(typeof(PlaceOrder), typeof(Fault<ReserveInventory>));
        ThenTheSagaHistoryOutcomesAre(SagaHistoryOutcome.Started, SagaHistoryOutcome.Compensated);
    }
}
