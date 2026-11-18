using Bobcat;
using EventSourcedSagas.Fulfillment;
using EventSourcedSagas.Support;
using JasperFx.Events;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace EventSourcedSagas.Specs;

// GH-5415: a completed event-sourced saga keeps its stream and its snapshot. Wolverine checks for completion before any
// Handle runs, so a late message goes to NotFound, or is treated as a message for a missing saga is today (GH-5572).

[BobcatFeature("Messages for a completed event-sourced saga")]
[Collection(nameof(FulfillmentCollection))]
public class messages_for_a_completed_event_sourced_saga(InlineFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    [BobcatSpec(typeof(CarrierConfirmedShipment), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_late_shipment_goes_to_NotFound()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), new OrderFulfillmentCancelled("Payment timed out"), Completed);

        await WhenReceived(new CarrierConfirmedShipment(OrderFulfillmentId, "1Z999AA10123456784"));

        ThenNoEvents();
        ThenMessageSent(new ShipmentForClosedOrder(OrderFulfillmentId, "1Z999AA10123456784"));
        await ThenTheSagaIsCompleted<OrderFulfillment>(OrderFulfillmentId);
    }

    [BobcatSpec(typeof(PaymentReceived), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_late_payment_with_no_NotFound_is_dead_lettered_as_a_message_for_a_missing_saga()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), new OrderFulfillmentCancelled("Payment timed out"), Completed);

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 100m));

        ThenNoEvents();
        ThenDeadLettered<PaymentReceived, UnknownSagaException>(by: FulfillmentServices.ServiceName);
        await ThenTheSagaIsCompleted<OrderFulfillment>(OrderFulfillmentId);
    }

    [BobcatSpec(typeof(OrderPlaced), Lifecycle = Lifecycle.Acceptance)]
    public async Task an_OrderPlaced_does_not_restart_a_completed_fulfillment()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid(), Reserved, Shipped, Completed);

        await WhenReceived(new OrderPlaced(OrderFulfillmentId, CustomerId, 100m));

        ThenNoEvents();
        await ThenTheSagaIsCompleted<OrderFulfillment>(OrderFulfillmentId);
    }
}

[BobcatFeature("An event-sourced saga completes with a SagaCompleted event")]
[Collection(nameof(FulfillmentCollection))]
public class completing_an_event_sourced_saga(InlineFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    private Guid TicketId { get; } = Guid.NewGuid();

    // SupportTicket.Handle(CloseTicket) only calls MarkCompleted(). The saga type is marked [ArchiveWhenCompleted]
    [BobcatSpec(typeof(CloseTicket), Lifecycle = Lifecycle.Acceptance)]
    public async Task MarkCompleted_with_no_events_appends_SagaCompleted_and_archives_the_stream()
    {
        await GivenEvents<SupportTicket>(TicketId, new TicketOpened("The parcel never arrived"));

        await WhenReceived(new CloseTicket(TicketId));

        ThenEvents(new SagaCompleted(), new Archived(ArchiveWhenCompletedAttribute.Reason));
        await ThenTheSagaStreamIsArchived<SupportTicket>(TicketId);
    }

    // JasperFx.Events' Archived event counts as completion too, so a stream someone archived by hand is done
    [BobcatSpec(typeof(CarrierConfirmedShipment), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_fulfillment_whose_stream_was_archived_by_hand_is_completed()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), new Archived("cancelled by support"));

        await WhenReceived(new CarrierConfirmedShipment(OrderFulfillmentId, "1Z999AA10123456784"));

        ThenNoEvents();
        ThenMessageSent(new ShipmentForClosedOrder(OrderFulfillmentId, "1Z999AA10123456784"));
    }
}
