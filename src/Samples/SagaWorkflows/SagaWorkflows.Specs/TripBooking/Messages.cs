using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;

namespace SagaWorkflows.TripBooking;

// The trip booking of Restate's sagas example (restatedev/examples, patterns-use-cases/src/sagas): book a flight, a car
// and a hotel in turn. A refusal undoes the bookings in reverse order, each cancellation finished before the next one
// starts. Restate registers each cancellation BEFORE its booking, because a booking that failed may still have gone
// through on the other side.

public record BookTrip([property: SagaIdentity] Guid TripId, string CustomerId);

public record BookFlight([property: SagaIdentity] Guid TripId, string CustomerId);

public record BookCar([property: SagaIdentity] Guid TripId, string CustomerId);

public record BookHotel([property: SagaIdentity] Guid TripId, string CustomerId);

public record FlightBooked([property: SagaIdentity] Guid TripId);

public record CarBooked([property: SagaIdentity] Guid TripId);

public record HotelBooked([property: SagaIdentity] Guid TripId);

// The undo steps, and the replies a provider sends when it has cancelled
public record CancelFlight([property: SagaIdentity] Guid TripId, string CustomerId);

public record CancelCar([property: SagaIdentity] Guid TripId, string CustomerId);

public record CancelHotel([property: SagaIdentity] Guid TripId, string CustomerId);

public record FlightCancelled([property: SagaIdentity] Guid TripId);

public record CarCancelled([property: SagaIdentity] Guid TripId);

public record TripBooked([property: SagaIdentity] Guid TripId);

// A provider said no. Retrying won't change its mind (Restate's TerminalError)
[TerminalFailure]
public class BookingRefusedException(string message) : Exception(message);

// The hotel has no rooms, but the flight is still worth keeping
[TerminalFailure]
public class NoRoomsException(string message) : BookingRefusedException(message);

// A provider refused to cancel a booking
[TerminalFailure]
public class CancellationRefusedException(string message) : Exception(message);
