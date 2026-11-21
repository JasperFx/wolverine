using JasperFx;
using Wolverine;

namespace SagaWorkflows.TripBooking;

// GH-5673: the trip booking on the undo stack. Each booking pushes its cancellation first, so a booking whose outcome
// is unknown is cancelled too, and the step's reply confirms it. A refusal unwinds the stack one cancellation at a time:
// the flight and car cancellations are confirmed by the provider's own reply, the hotel's by Wolverine's
// acknowledgement. The evaluation's hand-written state machine (an Attempted list, three Handle(XCancelled) methods and
// an undoNext()) is gone.
#region sample_undo_stack_trip
public class Trip : CompensatingSaga, IRevisioned
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = "";

    public static (Trip, BookFlight) Start(BookTrip command)
    {
        var trip = new Trip { Id = command.TripId, CustomerId = command.CustomerId };
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

    // TripBooked is what the trip says when it's finished, so the catch-all below doesn't cover it
    [CompletesSaga]
    public TripBooked Handle(HotelBooked booked)
    {
        MarkCompleted();
        return new TripBooked(Id);
    }

    // A refusal, wherever the provider runs: cancel everything, newest first, each one confirmed before the next
    public UndoSequence Compensate(object failed, RemoteFailure<BookingRefusedException> refused) => UndoSequentially();

    // No cars at all: the trip can't happen. Cancel everything one at a time as above, then close the trip
    public UndoSequence Compensate(BookCar failed, RemoteFailure<NoCarsException> noCars)
        => UndoSequentially(completeWhenDone: true);

    // No rooms: give the car back too, all at once, but keep the flight
    public OutgoingMessages Compensate(BookHotel failed, RemoteFailure<NoRoomsException> noRooms) => UndoMessagesSince("car");
}
#endregion
