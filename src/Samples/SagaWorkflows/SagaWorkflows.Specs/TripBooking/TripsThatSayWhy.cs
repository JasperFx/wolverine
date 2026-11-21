using JasperFx;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace SagaWorkflows.TripBooking;

// GH-5698: a Compensate method can return its undo sequence in a tuple with whatever else it has to say: an event for
// an event-sourced saga's stream, a message to send. The unwind is the same as returning the undo sequence alone: one
// cancellation at a time, the saga completed at the end when it asks for that, and every cancellation in the
// compensation log.

// The event an event-sourced trip appends when it starts to unwind, and the message a trip sends the traveller
public record TripUnwindStarted(string Reason);

public record NotifyTraveller([property: SagaIdentity] Guid TripId, string Reason);

#region sample_compensate_returns_an_event_with_the_undo_sequence
public class EventSourcedTripThatSaysWhy : EventSourcedSaga
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = "";

    public static (TripStarted, SagaUndoPushed, OutgoingMessages) Start(BookTrip command)
        => (new TripStarted(command.TripId, command.CustomerId),
            PushedUndo(new CancelFlight(command.TripId, command.CustomerId), "flight", confirmedBy: typeof(FlightCancelled)),
            [new BookFlight(command.TripId, command.CustomerId)]);

    public OutgoingMessages Handle(FlightBooked booked)
    {
        PushUndo(new CancelCar(Id, CustomerId), "car", confirmedBy: typeof(CarCancelled));
        return [new BookCar(Id, CustomerId)];
    }

    public OutgoingMessages Handle(CarBooked booked)
    {
        PushUndo(new CancelHotel(Id, CustomerId), "hotel");
        return [new BookHotel(Id, CustomerId)];
    }

    public (SagaCompleted, OutgoingMessages) Handle(HotelBooked booked) => (new SagaCompleted(), [new TripBooked(Id)]);

    // A refusal: the event, then the unwind. The trip stays open, Compensated
    [Compensates(typeof(BookFlight), typeof(BookCar), typeof(BookHotel))]
    public (TripUnwindStarted, UndoSequence) Compensate(object failed, RemoteFailure<BookingRefusedException> refused)
        => (new TripUnwindStarted("A provider refused a booking"), UndoSequentially());

    // No cars: the unwind first this time, and the trip closes once the last cancellation is confirmed
    public (UndoSequence, TripUnwindStarted) Compensate(BookCar failed, RemoteFailure<NoCarsException> noCars)
        => (UndoSequentially(completeWhenDone: true), new TripUnwindStarted("No cars"));

    // No rooms: the event, the unwind, and a message for the traveller
    public (TripUnwindStarted, UndoSequence, OutgoingMessages) Compensate(BookHotel failed,
        RemoteFailure<NoRoomsException> noRooms)
        => (new TripUnwindStarted("No rooms"), UndoSequentially(completeWhenDone: true),
            [new NotifyTraveller(Id, "No rooms")]);

    public void Apply(TripStarted e)
    {
        Id = e.TripId;
        CustomerId = e.CustomerId;
    }
}
#endregion

// The same tuples on a document saga, where the first member is a message to send rather than an event
public class TripThatSaysWhy : CompensatingSaga, IRevisioned
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = "";

    public static (TripThatSaysWhy, BookFlight) Start(BookTrip command)
    {
        var trip = new TripThatSaysWhy { Id = command.TripId, CustomerId = command.CustomerId };
        trip.PushUndo(new CancelFlight(trip.Id, trip.CustomerId), "flight", confirmedBy: typeof(FlightCancelled));
        return (trip, new BookFlight(trip.Id, trip.CustomerId));
    }

    public BookCar Handle(FlightBooked booked)
    {
        PushUndo(new CancelCar(Id, CustomerId), "car", confirmedBy: typeof(CarCancelled));
        return new BookCar(Id, CustomerId);
    }

    public BookHotel Handle(CarBooked booked)
    {
        PushUndo(new CancelHotel(Id, CustomerId), "hotel");
        return new BookHotel(Id, CustomerId);
    }

    public TripBooked Handle(HotelBooked booked)
    {
        MarkCompleted();
        return new TripBooked(Id);
    }

    // A refusal: tell the traveller, then unwind. The trip stays open, Compensated
    public (NotifyTraveller, UndoSequence) Compensate(object failed, RemoteFailure<BookingRefusedException> refused)
        => (new NotifyTraveller(Id, "A provider refused a booking"), UndoSequentially());

    // No cars: unwind, tell the traveller, and close the trip once the last cancellation is confirmed
    public (UndoSequence, NotifyTraveller) Compensate(BookCar failed, RemoteFailure<NoCarsException> noCars)
        => (UndoSequentially(completeWhenDone: true), new NotifyTraveller(Id, "No cars"));
}
