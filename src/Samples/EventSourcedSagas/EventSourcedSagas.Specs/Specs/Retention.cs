using Bobcat;
using JasperFx.Core;
using EventSourcedSagas.Fulfillment;

namespace EventSourcedSagas.Specs;

// GH-5415 with GH-5572: the fulfillment service retains completed sagas for 30 days. When that has passed, the same
// sweep that deletes a retained document saga archives an event-sourced saga's stream and removes its snapshot. Events
// are never deleted.

// "A completed event-sourced saga's stream is archived after its retention period": run on Marten, Polecat and Fisher by the classes in StoreParity.cs
public abstract class retaining_a_completed_event_sourced_saga(FulfillmentServices services) : EventSourcedSagaSpec(services)
{
    [BobcatSpec]
    public async Task a_completed_fulfillment_is_archived_once_its_retention_period_has_passed()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid(), Reserved, Shipped, Completed);

        await WhenCompletedSagasAreSwept<OrderFulfillment>(31.Days());

        await ThenTheSagaStreamIsArchived<OrderFulfillment>(OrderFulfillmentId);
        await ThenNoSaga<OrderFulfillment>(OrderFulfillmentId);
    }

    [BobcatSpec]
    public async Task a_completed_fulfillment_is_kept_during_its_retention_period()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid(), Reserved, Shipped, Completed);

        await WhenCompletedSagasAreSwept<OrderFulfillment>(TimeSpan.Zero);

        await ThenTheSagaStreamIsNotArchived<OrderFulfillment>(OrderFulfillmentId);
        await ThenTheSagaIsCompleted<OrderFulfillment>(OrderFulfillmentId);
    }

    [BobcatSpec]
    public async Task an_open_fulfillment_is_never_archived()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid());

        await WhenCompletedSagasAreSwept<OrderFulfillment>(31.Days());

        await ThenTheSagaStreamIsNotArchived<OrderFulfillment>(OrderFulfillmentId);
        await ThenTheSagaIsStillOpen<OrderFulfillment>(OrderFulfillmentId);
    }
}
