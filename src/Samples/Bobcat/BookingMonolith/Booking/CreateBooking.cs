using FluentValidation;
using Marten;
using BookingMonolith;
using Flight;
using Microsoft.AspNetCore.Http.HttpResults;
using Passenger;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Booking;

public record CreateBooking(Guid PassengerId, Guid FlightId, string? Description)
{
    public class Validator : AbstractValidator<CreateBooking>
    {
        public Validator()
        {
            RuleFor(x => x.PassengerId).NotEmpty();
            RuleFor(x => x.FlightId).NotEmpty();
        }
    }
}

public static class CreateBookingEndpoint
{
    // Event-sourced: starts a new event stream in Marten.
    // Replaces: gRPC calls to Flight/Passenger + EventStoreDB append + MongoDB projection
    [WolverinePost("/api/bookings")]

    // Multiple [Entity] parameters enable Marten batch querying — Wolverine loads
    // both the PassengerRecord and FlightRecord in a single database round-trip
    // instead of two sequential LoadAsync calls. This is a significant performance win.
    // OnMissing = ProblemDetailsWith400 treats missing referenced entities as bad input
    // (the IDs come from the client), eliminating the need for a separate ValidateAsync.
    //
    // First tuple item is the HTTP response (an IResult, executed as-is); the rest are cascaded
    // messages. See RegisterUserEndpoint.
    public static (Created<BookingRecord>, BookingCreated) Post(
        CreateBooking command,
        [Entity(Required = true, OnMissing = OnMissing.ProblemDetailsWith400)] Passenger.Passenger passenger,
        [Entity(Required = true, OnMissing = OnMissing.ProblemDetailsWith400)] Flight.Flight flight,
        IDocumentSession session)
    {
        var bookingId = Guid.NewGuid();

        var domainEvent = new BookingCreatedDomainEvent(
            bookingId,
            passenger.Id,
            passenger.Name,
            flight.Id,
            flight.FlightNumber,
            flight.Price,
            command.Description,
            SeatNumber: null
        );

        // Start an event stream — Marten stores the event and
        // the BookingRecord snapshot is built via Apply methods
        session.Events.StartStream<BookingRecord>(bookingId, domainEvent);

        // Build the snapshot manually for the response
        var booking = new BookingRecord();
        booking.Apply(domainEvent);

        return (
            TypedResults.Created($"/api/bookings/{bookingId}", booking),
            new BookingCreated(bookingId, passenger.Id, flight.Id));
    }
}

public static class GetBookingsEndpoint
{
    [WolverineGet("/api/bookings")]
    public static Task<IReadOnlyList<BookingRecord>> Get(IQuerySession session, CancellationToken ct)
        => session.Query<BookingRecord>().ToListAsync(ct);
}

public static class GetBookingByIdEndpoint
{
    [WolverineGet("/api/bookings/{id}")]
    public static BookingRecord? Get(Guid id, [Wolverine.Persistence.Entity] BookingRecord? booking) => booking;
}
