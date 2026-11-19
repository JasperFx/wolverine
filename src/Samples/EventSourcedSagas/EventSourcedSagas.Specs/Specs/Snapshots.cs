using Bobcat;
using EventSourcedSagas.Fulfillment;
using JasperFx.Events.Projections;

namespace EventSourcedSagas.Specs;

// GH-5415: Wolverine registers each event-sourced saga as an Inline snapshot, written in the same transaction as its
// events. An application's own registration wins: Live works, but the saga explorer can then only open one at a time.
// Async is refused at startup (see identity_and_registration).

// "An event-sourced saga is kept as an Inline snapshot": run on Marten, Polecat and Fisher by the classes in StoreParity.cs
public abstract class an_inline_snapshot(FulfillmentServices services) : EventSourcedSagaSpec(services)
{
    [BobcatSpec(typeof(StartOrderFulfillment))]
    public async Task the_snapshot_is_written_with_the_events_and_the_saga_explorer_can_search_it()
    {
        await WhenReceived(new StartOrderFulfillment(OrderFulfillmentId, CustomerId, 100m));

        await ThenTheSagaIsKeptAs<OrderFulfillment>(ProjectionLifecycle.Inline);
        await ThenTheSagaSnapshot<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>()
            .With(x => x.TotalAmount, 100m)
            .With(x => x.Version, 1));
        await ThenTheSagaExplorerShows<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>()
            .With(x => x.CustomerId, CustomerId));
        ThenTheSagaExplorerCanSearch<OrderFulfillment>();
    }
}

[BobcatFeature("An event-sourced saga the application registers as a Live aggregation")]
[Collection(nameof(LiveFulfillmentCollection))]
public class a_live_aggregation(LiveFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_live_saga_still_works_and_the_saga_explorer_opens_it_but_cannot_search()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started());

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 100m));

        ThenEvents(new PaymentConfirmed(100m));
        await ThenTheSagaIsKeptAs<OrderFulfillment>(ProjectionLifecycle.Live);
        await ThenAggregate<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>().With(x => x.IsPaid, true));
        await ThenTheSagaExplorerShows<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>().With(x => x.IsPaid, true));
        ThenTheSagaExplorerCanSearch<OrderFulfillment>(can: false);
    }
}
