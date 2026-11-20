using Bobcat;
using SagaWorkflows.Providers;
using SagaWorkflows.TripBooking;
using Wolverine;

namespace SagaWorkflows.Specs;

// GH-5698: a Compensate method that returns its undo sequence in a tuple -- with an event for an event-sourced saga's
// stream, a message to send, or both -- unwinds exactly as one that returns the undo sequence alone. The cancellations
// go one at a time, the trip completes at the end when UndoSequentially(completeWhenDone: true) asks for it, and the
// compensation log has every cancellation. The rest of the tuple is appended or sent as it would be without one.

[BobcatFeature("An event-sourced trip returns an event with its undo sequence")]
[Collection(nameof(EventSourcedTripThatSaysWhyCollection))]
public class an_event_with_the_undo_sequence(EventSourcedTripThatSaysWhyServices services) : SagaWorkflowSpec(services)
{
    private readonly Guid _tripId = Guid.NewGuid();
    private readonly string _customer = $"c-{Guid.NewGuid():N}";

    // (TripUnwindStarted, UndoSequentially())
    [BobcatSpec(typeof(BookTrip))]
    public async Task the_event_first_and_the_trip_stays_open()
    {
        ProviderRefuses(Booking.Hotel, _customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        await ThenTheSagaStreamHolds(_tripId, new TripUnwindStarted("A provider refused a booking"));
        ThenUndoneOneAtATime(typeof(CancelHotel), typeof(CancelCar), typeof(CancelFlight));
        ThenNoBooking(_customer, Booking.Flight);
        await ThenTheSagaIsStillOpen<EventSourcedTripThatSaysWhy>(_tripId);
        await ThenTheSagaStatusIs<EventSourcedTripThatSaysWhy>(_tripId, SagaStatus.Compensated);
        await ThenTheLastCompensationUndid<EventSourcedTripThatSaysWhy>(_tripId, "hotel", "car", "flight");
        await ThenTheUndoStackHolds<EventSourcedTripThatSaysWhy>(_tripId);
    }

    // (UndoSequentially(completeWhenDone: true), TripUnwindStarted)
    [BobcatSpec(typeof(BookTrip))]
    public async Task the_undo_sequence_first_and_the_trip_completes_when_done()
    {
        NoCarsFor(_customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        await ThenTheSagaStreamHolds(_tripId, new TripUnwindStarted("No cars"));
        ThenUndoneOneAtATime(typeof(CancelCar), typeof(CancelFlight));
        ThenNoBooking(_customer, Booking.Flight);
        await ThenTheSagaIsCompleted<EventSourcedTripThatSaysWhy>(_tripId);
        await ThenTheSagaStatusIs<EventSourcedTripThatSaysWhy>(_tripId, SagaStatus.Compensated);
        await ThenTheLastCompensationUndid<EventSourcedTripThatSaysWhy>(_tripId, "car", "flight");
    }

    // (TripUnwindStarted, UndoSequentially(completeWhenDone: true), [NotifyTraveller])
    [BobcatSpec(typeof(BookTrip))]
    public async Task an_event_the_undo_sequence_and_a_message_together()
    {
        HotelIsFull(_customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        await ThenTheSagaStreamHolds(_tripId, new TripUnwindStarted("No rooms"));
        ThenMessageSent(new NotifyTraveller(_tripId, "No rooms"));
        ThenUndoneOneAtATime(typeof(CancelHotel), typeof(CancelCar), typeof(CancelFlight));
        await ThenTheSagaIsCompleted<EventSourcedTripThatSaysWhy>(_tripId);
        await ThenTheLastCompensationUndid<EventSourcedTripThatSaysWhy>(_tripId, "hotel", "car", "flight");
    }
}

[BobcatFeature("A trip sends a message with its undo sequence")]
[Collection(nameof(DocumentTripThatSaysWhyCollection))]
public class a_message_with_the_undo_sequence(DocumentTripThatSaysWhyServices services) : SagaWorkflowSpec(services)
{
    private readonly Guid _tripId = Guid.NewGuid();
    private readonly string _customer = $"c-{Guid.NewGuid():N}";

    // (NotifyTraveller, UndoSequentially())
    [BobcatSpec(typeof(BookTrip))]
    public async Task the_message_first_and_the_trip_stays_open()
    {
        ProviderRefuses(Booking.Hotel, _customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        ThenMessageSent(new NotifyTraveller(_tripId, "A provider refused a booking"));
        ThenUndoneOneAtATime(typeof(CancelHotel), typeof(CancelCar), typeof(CancelFlight));
        ThenNoBooking(_customer, Booking.Flight);
        await ThenTheSagaStatusIs<TripThatSaysWhy>(_tripId, SagaStatus.Compensated);
        await ThenTheLastCompensationUndid<TripThatSaysWhy>(_tripId, "hotel", "car", "flight");
        await ThenTheUndoStackHolds<TripThatSaysWhy>(_tripId);
    }

    // (UndoSequentially(completeWhenDone: true), NotifyTraveller)
    [BobcatSpec(typeof(BookTrip))]
    public async Task the_undo_sequence_first_and_the_trip_completes_when_done()
    {
        NoCarsFor(_customer);

        await WhenReceived(new BookTrip(_tripId, _customer));

        ThenMessageSent(new NotifyTraveller(_tripId, "No cars"));
        ThenUndoneOneAtATime(typeof(CancelCar), typeof(CancelFlight));
        ThenNoBooking(_customer, Booking.Flight);
        await ThenNoSaga<TripThatSaysWhy>(_tripId);
    }
}
