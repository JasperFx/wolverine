using Bobcat;
using SagaWorkflows.MoneyTransfer;
using SagaWorkflows.Providers;
using SagaWorkflows.TripBooking;
using Wolverine;

namespace SagaWorkflows.Specs;

// GH-5673: the undo stack on CompensatingSaga. A saga pushes an undo message as it goes, tagged by the step that pushed
// it; a Compensate method decides how much a failure unwinds: everything (UndoMessages), a part of it
// (UndoMessagesSince), or everything one confirmed undo message at a time (UndoSequentially). The compensation log
// records the failure each compensation answered and the undo messages it sent.

[BobcatFeature("A trip unwinds its bookings one cancellation at a time")]
[Collection(nameof(DocumentSagaCollection))]
public class unwinding_a_trip(DocumentSagaServices services) : SagaWorkflowSpec(services)
{
    private readonly Guid _tripId = Guid.NewGuid();
    private readonly string _customer = $"c-{Guid.NewGuid():N}";

    // The hotel's cancellation was pushed before its booking, so the refused booking is cancelled too: its outcome on
    // the provider's side is unknown. Flight and car are confirmed by the provider's reply, the hotel by an acknowledgement
    [BobcatSpec(typeof(BookTrip))]
    public async Task a_refused_hotel_cancels_every_booking_newest_first_each_confirmed_before_the_next()
    {
        ProviderRefuses(Booking.Hotel, _customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        ThenUndoneOneAtATime(typeof(CancelHotel), typeof(CancelCar), typeof(CancelFlight));
        ThenNoBooking(_customer, Booking.Flight);
        ThenNoBooking(_customer, Booking.Car);
        await ThenTheSagaStatusIs<Trip>(_tripId, SagaStatus.Compensated);
        await ThenTheSagaCompensated<Trip, BookHotel, BookingRefusedException>(_tripId);
        await ThenTheLastCompensationUndid<Trip>(_tripId, "hotel", "car", "flight");
        await ThenTheUndoStackHolds<Trip>(_tripId);
    }

    // UndoSequentially(completeWhenDone: true): once the last cancellation is confirmed, the trip completes. By default
    // (above) it stays open, Compensated
    [BobcatSpec(typeof(BookTrip))]
    public async Task no_cars_cancels_every_booking_and_then_closes_the_trip()
    {
        NoCarsFor(_customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        ThenUndoneOneAtATime(typeof(CancelCar), typeof(CancelFlight));
        ThenNoBooking(_customer, Booking.Flight);
        await ThenNoSaga<Trip>(_tripId);
    }

    [BobcatSpec(typeof(BookTrip))]
    public async Task a_refused_flight_only_cancels_the_flight()
    {
        ProviderRefuses(Booking.Flight, _customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        ThenHandled<CancelFlight>();
        ThenNoMessageSent<CancelCar>();
        ThenNoMessageSent<CancelHotel>();
        await ThenTheLastCompensationUndid<Trip>(_tripId, "flight");
    }

    // The car's cancellation fails for good: its fault comes back, the unwind stops there, and the flight's cancellation
    // is still on the stack for whoever retries the unwind
    [BobcatSpec(typeof(BookTrip))]
    public async Task a_cancellation_that_fails_leaves_the_trip_compensation_failed_with_the_rest_of_the_stack()
    {
        ProviderRefuses(Booking.Hotel, _customer);
        ProviderRefusesToCancel(Booking.Car, _customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        ThenDeadLettered<CancelCar>(by: WorkflowServices.ProvidersName);
        ThenNoMessageSent<CancelFlight>();
        ThenStillBooked(_customer, Booking.Flight);
        await ThenTheSagaStatusIs<Trip>(_tripId, SagaStatus.CompensationFailed);
        await ThenTheUndoStackHolds<Trip>(_tripId, "flight", "car");
        await ThenTheLastCompensationUndid<Trip>(_tripId, "hotel");
    }

    // A partial unwind: Compensate(BookHotel, RemoteFailure<NoRoomsException>) returns UndoMessagesSince("car")
    [BobcatSpec(typeof(BookTrip))]
    public async Task no_rooms_gives_back_the_car_but_keeps_the_flight()
    {
        HotelIsFull(_customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        ThenMessageSent(new CancelHotel(_tripId, _customer));
        ThenMessageSent(new CancelCar(_tripId, _customer));
        ThenNoMessageSent<CancelFlight>();
        ThenStillBooked(_customer, Booking.Flight);
        await ThenTheSagaCompensated<Trip, BookHotel, NoRoomsException>(_tripId);
        await ThenTheUndoStackHolds<Trip>(_tripId, "flight");
    }

    [BobcatSpec(typeof(BookTrip))]
    public async Task a_trip_that_books_everything_cancels_nothing()
    {
        await WhenReceived(new BookTrip(_tripId, _customer));

        ThenMessageSent(new TripBooked(_tripId));
        ThenNoMessageSent<CancelFlight>();
        ThenStillBooked(_customer, Booking.Hotel);
    }
}

[BobcatFeature("An event-sourced trip unwinds its bookings one cancellation at a time")]
[Collection(nameof(EventSourcedSagaCollection))]
public class unwinding_an_event_sourced_trip(EventSourcedSagaServices services) : SagaWorkflowSpec(services)
{
    private readonly Guid _tripId = Guid.NewGuid();
    private readonly string _customer = $"c-{Guid.NewGuid():N}";

    [BobcatSpec(typeof(BookTrip))]
    public async Task a_refused_hotel_cancels_every_booking_newest_first_each_confirmed_before_the_next()
    {
        ProviderRefuses(Booking.Hotel, _customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        ThenUndoneOneAtATime(typeof(CancelHotel), typeof(CancelCar), typeof(CancelFlight));
        ThenNoBooking(_customer, Booking.Flight);
        await ThenTheSagaStatusIs<EventSourcedTrip>(_tripId, SagaStatus.Compensated);
        await ThenTheLastCompensationUndid<EventSourcedTrip>(_tripId, "hotel", "car", "flight");
        await ThenTheUndoStackHolds<EventSourcedTrip>(_tripId);
    }

    // The event-sourced trip completes with Wolverine's SagaCompleted event, after the last cancellation's events
    [BobcatSpec(typeof(BookTrip))]
    public async Task no_cars_cancels_every_booking_and_then_closes_the_trip()
    {
        NoCarsFor(_customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        ThenUndoneOneAtATime(typeof(CancelCar), typeof(CancelFlight));
        await ThenTheSagaIsCompleted<EventSourcedTrip>(_tripId);
        await ThenTheSagaStatusIs<EventSourcedTrip>(_tripId, SagaStatus.Compensated);
    }

    [BobcatSpec(typeof(BookTrip))]
    public async Task a_refused_hotel_leaves_the_trip_open_by_default()
    {
        ProviderRefuses(Booking.Hotel, _customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        await ThenTheSagaIsStillOpen<EventSourcedTrip>(_tripId);
    }

    [BobcatSpec(typeof(BookTrip))]
    public async Task a_cancellation_that_fails_leaves_the_trip_compensation_failed_with_the_rest_of_the_stack()
    {
        ProviderRefuses(Booking.Hotel, _customer);
        ProviderRefusesToCancel(Booking.Car, _customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        ThenNoMessageSent<CancelFlight>();
        await ThenTheSagaStatusIs<EventSourcedTrip>(_tripId, SagaStatus.CompensationFailed);
        await ThenTheUndoStackHolds<EventSourcedTrip>(_tripId, "flight", "car");
    }
}

// The money transfer pushes each undo after its step's reply says the step happened
[BobcatFeature("A transfer undoes what happened, from its undo stack")]
[Collection(nameof(DocumentSagaCollection))]
public class undoing_a_transfer(DocumentSagaServices services) : SagaWorkflowSpec(services)
{
    private readonly Guid _transferId = Guid.NewGuid();

    [BobcatSpec(typeof(TransferMoney))]
    public async Task the_compensation_log_records_the_failure_and_the_undo_messages_sent()
    {
        AccountHolds("alice", 500m);
        TheLedgerRefuses(_transferId);

        await WhenReceived(new TransferMoney(_transferId, "alice", "bob", 100m));

        await ThenTheSagaCompensated<Transfer, RecordTransfer, LedgerUnavailableException>(_transferId);
        await ThenTheLastCompensationUndid<Transfer>(_transferId, "deposit", "withdrawal");
        await ThenTheUndoStackHolds<Transfer>(_transferId);
    }
}

[BobcatFeature("An event-sourced transfer undoes what happened, from its undo stack")]
[Collection(nameof(EventSourcedSagaCollection))]
public class undoing_an_event_sourced_transfer(EventSourcedSagaServices services) : SagaWorkflowSpec(services)
{
    private readonly Guid _transferId = Guid.NewGuid();

    [BobcatSpec(typeof(TransferMoney))]
    public async Task the_compensation_log_records_the_failure_and_the_undo_messages_sent()
    {
        AccountHolds("alice", 500m);
        TheLedgerRefuses(_transferId);

        await WhenReceived(new TransferMoney(_transferId, "alice", "bob", 100m));

        await ThenTheSagaCompensated<EventSourcedTransfer, RecordTransfer, LedgerUnavailableException>(_transferId);
        await ThenTheLastCompensationUndid<EventSourcedTransfer>(_transferId, "deposit", "withdrawal");
        await ThenTheUndoStackHolds<EventSourcedTransfer>(_transferId);
    }
}
