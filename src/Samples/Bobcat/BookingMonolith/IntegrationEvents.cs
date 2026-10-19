namespace BookingMonolith;

// Integration events for cross-module communication.
// Replaces: EventDispatcher + IEventMapper + PersistMessageProcessor +
// outbox/inbox tables + MassTransit consumers + gRPC inter-module calls

public record UserCreated(Guid UserId, string Email, string FirstName, string LastName);
public record PassengerCreated(Guid PassengerId, string Name);
public record FlightCreated(Guid FlightId, string FlightNumber, decimal Price, DateTime FlightDate);
public record BookingCreated(Guid BookingId, Guid PassengerId, Guid FlightId);
