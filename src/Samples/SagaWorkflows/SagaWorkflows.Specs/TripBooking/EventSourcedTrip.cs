using Wolverine;

namespace SagaWorkflows.TripBooking;

// GH-5673: the same trip as an event-sourced saga. The undo stack lives in the saga's stream as Wolverine's own events
// (wolverine_saga_undo_pushed, _sent, _completed, ...), so the stream's history shows every push and every cancellation.
// Start is static and has no saga to call PushUndo on, so it returns PushedUndo(...) among its events.

public record TripStarted(Guid TripId, string CustomerId);

public class EventSourcedTrip : EventSourcedSaga
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

    // The steps go out in OutgoingMessages, so [Compensates] names them for the catch-all
    [Compensates(typeof(BookFlight), typeof(BookCar), typeof(BookHotel))]
    public UndoSequence Compensate(object failed, RemoteFailure<BookingRefusedException> refused) => UndoSequentially();

    public void Apply(TripStarted e)
    {
        Id = e.TripId;
        CustomerId = e.CustomerId;
    }
}
