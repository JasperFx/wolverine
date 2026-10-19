using Marten.Events.Aggregation;

namespace Booking;

// --- Domain Events (stored in Marten event store) ---
// Replaces EventStoreDB streams + MongoDB read model projections

public record BookingCreatedDomainEvent(
    Guid BookingId,
    Guid PassengerId,
    string PassengerName,
    Guid FlightId,
    string FlightNumber,
    decimal Price,
    string? Description,
    string? SeatNumber
);

/// <summary>
/// Booking aggregate — event-sourced via Marten.
/// Replaces EventStoreDB + IEventStoreDBRepository + AggregateEventSourcing base class.
/// Marten rebuilds state by calling Apply methods automatically.
/// </summary>
public class BookingRecord
{
    public Guid Id { get; set; }
    public Guid PassengerId { get; set; }
    public string PassengerName { get; set; } = string.Empty;
    public Guid FlightId { get; set; }
    public string FlightNumber { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string? Description { get; set; }
    public string? SeatNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public void Apply(BookingCreatedDomainEvent e)
    {
        Id = e.BookingId;
        PassengerId = e.PassengerId;
        PassengerName = e.PassengerName;
        FlightId = e.FlightId;
        FlightNumber = e.FlightNumber;
        Price = e.Price;
        Description = e.Description;
        SeatNumber = e.SeatNumber;
        CreatedAt = DateTimeOffset.UtcNow;
    }
}
