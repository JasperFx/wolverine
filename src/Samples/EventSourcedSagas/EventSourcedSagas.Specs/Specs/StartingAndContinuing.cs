using Bobcat;
using EventSourcedSagas.Fulfillment;
using Wolverine;

namespace EventSourcedSagas.Specs;

// GH-5415: an event-sourced saga starts from events alone, and continues from its own stream.

[BobcatFeature("An event-sourced saga starts from events alone")]
[Collection(nameof(FulfillmentCollection))]
public class starting_an_event_sourced_saga(InlineFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    [BobcatSpec(typeof(StartOrderFulfillment))]
    public async Task starting_a_fulfillment_appends_its_first_event_and_schedules_the_payment_timeout()
    {
        await WhenReceived(new StartOrderFulfillment(OrderFulfillmentId, CustomerId, 100m));

        await ThenStreamIsStartedWithEvents<OrderFulfillment>(new OrderFulfillmentStarted(OrderFulfillmentId, CustomerId, 100m));
        ThenMessageScheduled(new PaymentTimeout(OrderFulfillmentId));
        await ThenSaga<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>()
            .With(x => x.CustomerId, CustomerId)
            .With(x => x.TotalAmount, 100m)
            .With(x => x.Status, SagaStatus.Active)
            .With(x => x.Version, 1));
        await ThenTheSagaIsStillOpen<OrderFulfillment>(OrderFulfillmentId);
    }

    // A saga start for an id that already exists is refused today, and an event-sourced one is no different. The store's
    // ExistingStreamIdCollisionException never reaches the application
    [BobcatSpec(typeof(StartOrderFulfillment))]
    public async Task a_second_start_for_the_same_fulfillment_is_refused_as_any_saga_start_is()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started());

        await WhenReceived(new StartOrderFulfillment(OrderFulfillmentId, CustomerId, 250m));

        ThenNoEvents();
        ThenDeadLettered<StartOrderFulfillment, SagaConcurrencyException>(by: FulfillmentServices.ServiceName);
        await ThenSaga<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>().With(x => x.TotalAmount, 100m));
    }

    // Another message starts the same stream between this start's load and its save. The store's own refusal is reported
    // as the SagaConcurrencyException a duplicate start is
    [BobcatSpec(typeof(StartOrderFulfillment))]
    public async Task a_start_that_loses_the_race_to_another_start_is_refused_as_any_saga_start_is()
    {
        WhileActingOn<OrderFulfillment>(OrderFulfillmentId, Started());

        await WhenReceived(new StartOrderFulfillment(OrderFulfillmentId, CustomerId, 250m));

        ThenNoEvents();
        ThenDeadLettered<StartOrderFulfillment, SagaConcurrencyException>(by: FulfillmentServices.ServiceName);
        await ThenSaga<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>().With(x => x.TotalAmount, 100m));
    }

    [BobcatSpec(typeof(OrderPlaced))]
    public async Task an_OrderPlaced_starts_a_fulfillment_that_has_not_started()
    {
        await WhenReceived(new OrderPlaced(OrderFulfillmentId, CustomerId, 100m));

        await ThenStreamIsStartedWithEvents<OrderFulfillment>(new OrderFulfillmentStarted(OrderFulfillmentId, CustomerId, 100m));
    }

    [BobcatSpec(typeof(OrderPlaced))]
    public async Task an_OrderPlaced_for_a_fulfillment_that_has_started_is_handled_like_any_other_message()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started());

        await WhenReceived(new OrderPlaced(OrderFulfillmentId, CustomerId, 100m));

        ThenNoEvents();
        ThenNotDeadLettered<OrderPlaced>();
        await ThenSaga<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>()
            .With(x => x.TotalAmount, 100m)
            .With(x => x.Version, 1));
    }
}

[BobcatFeature("An event-sourced saga continues from its own stream")]
[Collection(nameof(FulfillmentCollection))]
public class continuing_an_event_sourced_saga(InlineFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_payment_is_appended_to_the_fulfillments_own_stream()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started());

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 100m));

        ThenEvents(new PaymentConfirmed(100m));
        await ThenSaga<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>()
            .With(x => x.IsPaid, true)
            .With(x => x.Version, 2));
    }

    // The step's own guard holds because the snapshot is written in the same transaction as the events before it
    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_payment_received_twice_is_confirmed_once()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid());

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 100m));

        ThenNoEvents();
        ThenNotDeadLettered<PaymentReceived>();
        await ThenSaga<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>()
            .With(x => x.IsPaid, true)
            .With(x => x.Version, 2));
    }

    [BobcatSpec(typeof(WarehouseReservedItems))]
    public async Task a_reply_with_no_id_finds_its_fulfillment_by_the_saga_id_header()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid());

        await WhenReceived(new WarehouseReservedItems(TheReservation),
            options: new DeliveryOptions { SagaId = OrderFulfillmentId.ToString() });

        ThenEvents(new ItemsReserved(TheReservation));
    }

    [BobcatSpec(typeof(CarrierConfirmedShipment))]
    public async Task the_last_step_returns_SagaCompleted_which_completes_the_fulfillment()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid(), Reserved);

        await WhenReceived(new CarrierConfirmedShipment(OrderFulfillmentId, "1Z999AA10123456784"));

        ThenEvents(new ShipmentConfirmed("1Z999AA10123456784"), new SagaCompleted());
        await ThenTheSagaIsCompleted<OrderFulfillment>(OrderFulfillmentId);

        // Nothing is deleted on completion: the stream and the snapshot both stay
        await ThenTheSagaStreamIsNotArchived<OrderFulfillment>(OrderFulfillmentId);
        await ThenTheSagaSnapshot<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>()
            .With(x => x.IsShipped, true)
            .With(x => x.Version, 5));
    }

    [BobcatSpec(typeof(CancelOrderFulfillment))]
    public async Task MarkCompleted_completes_the_fulfillment_with_a_SagaCompleted_event()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started());

        await WhenReceived(new CancelOrderFulfillment(OrderFulfillmentId, "The customer changed their mind"));

        ThenEvents(new OrderFulfillmentCancelled("The customer changed their mind"), new SagaCompleted());
        await ThenTheSagaIsCompleted<OrderFulfillment>(OrderFulfillmentId);
        await ThenSaga<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>()
            .With(x => x.CancellationReason, "The customer changed their mind"));
    }
}
