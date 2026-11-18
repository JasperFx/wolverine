using Bobcat;
using EventSourcedSagas.Fulfillment;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.Util;

namespace EventSourcedSagas.Specs;

// GH-5415, for CritterWatch#1680: ISagaHistoryDiagnostics reads an event-sourced saga's stream a page at a time, and its
// state at a version or a point in time with the store's own time travel. No audit table, no extra writes.

// "An event-sourced saga's history": run on Marten, Polecat and Fisher by the classes in StoreParity.cs
public abstract class reading_an_event_sourced_sagas_history(FulfillmentServices services) : EventSourcedSagaSpec(services)
{
    [BobcatSpec]
    public async Task the_history_is_read_a_page_at_a_time()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid(), Reserved, Shipped, Completed);

        await WhenTheSagaHistoryIsRead<OrderFulfillment>(OrderFulfillmentId, page: 1, pageSize: 2);

        ThenTheSagaHistoryIs(typeof(OrderFulfillmentStarted), typeof(PaymentConfirmed));
        ThenTheSagaHistoryHasInAll(5);
        await ThenTheSagaSupportsHistory<OrderFulfillment>();
    }

    [BobcatSpec]
    public async Task the_last_page_of_the_history_holds_the_terminal_event()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid(), Reserved, Shipped, Completed);

        await WhenTheSagaHistoryIsRead<OrderFulfillment>(OrderFulfillmentId, page: 3, pageSize: 2);

        ThenTheSagaHistoryIs(typeof(SagaCompleted));
        ThenTheSagaHistoryHasInAll(5);
    }

    [BobcatSpec]
    public async Task the_saga_is_read_as_it_was_at_a_version()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid(), Reserved, Shipped, Completed);

        await WhenTheSagaIsReadAtVersion<OrderFulfillment>(OrderFulfillmentId, 2);

        ThenTheSagaWas(Specify<OrderFulfillment>()
            .With(x => x.Version, 2)
            .With(x => x.IsPaid, true)
            .With(x => x.AreItemsReserved, false)
            .With(x => x.CompletedAt, (DateTimeOffset?)null));
    }

    [BobcatSpec]
    public async Task the_saga_is_read_as_it_was_at_a_point_in_time()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid());
        var paidAt = await TheTimeOfTheLastEvent();
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Reserved, Shipped, Completed);

        await WhenTheSagaIsReadAsOf<OrderFulfillment>(OrderFulfillmentId, paidAt, "when it was paid");

        ThenTheSagaWas(Specify<OrderFulfillment>()
            .With(x => x.IsPaid, true)
            .With(x => x.IsShipped, false));
    }

    // Wolverine's own compensation events are history like any other, so time travel shows the status at every step
    [BobcatSpec]
    public async Task the_status_at_each_step_comes_from_the_compensation_events()
    {
        var step = typeof(PaymentReceived).ToMessageTypeName();
        var exception = typeof(LedgerUnavailableException).FullName!;
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId,
            Started(),
            new SagaCompensating(step, exception),
            new RefundIssued(100m),
            new SagaCompensated(step, exception));

        await WhenTheSagaIsReadAtVersion<OrderFulfillment>(OrderFulfillmentId, 3);

        ThenTheSagaWas(Specify<OrderFulfillment>()
            .With(x => x.Status, SagaStatus.Compensating)
            .With(x => x.IsRefunded, true));
    }

    [BobcatSpec]
    public async Task the_status_after_the_compensation_is_compensated()
    {
        var step = typeof(PaymentReceived).ToMessageTypeName();
        var exception = typeof(LedgerUnavailableException).FullName!;
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId,
            Started(),
            new SagaCompensating(step, exception),
            new RefundIssued(100m),
            new SagaCompensated(step, exception));

        await WhenTheSagaIsReadAtVersion<OrderFulfillment>(OrderFulfillmentId, 4);

        ThenTheSagaWas(Specify<OrderFulfillment>().With(x => x.Status, SagaStatus.Compensated));
    }
}
