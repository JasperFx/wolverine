using FluentValidation;
using Marten;
using BookingMonolith;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;

namespace Flight;

public record CreateFlight(
    string FlightNumber,
    Guid AircraftId,
    Guid DepartureAirportId,
    Guid ArriveAirportId,
    int DurationMinutes,
    decimal Price,
    DateTime DepartureDate,
    DateTime ArriveDate,
    DateTime FlightDate
)
{
    public class Validator : AbstractValidator<CreateFlight>
    {
        public Validator()
        {
            RuleFor(x => x.FlightNumber).NotEmpty();
            RuleFor(x => x.Price).GreaterThan(0);
            RuleFor(x => x.DepartureDate).LessThan(x => x.ArriveDate);
        }
    }
}

public static class CreateFlightEndpoint
{
    // First tuple item is the HTTP response (an IResult, executed as-is); the rest are cascaded
    // messages. See RegisterUserEndpoint.
    [WolverinePost("/api/flights")]
    public static (Created<Flight>, FlightCreated) Post(CreateFlight command, IDocumentSession session)
    {
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            FlightNumber = command.FlightNumber,
            AircraftId = command.AircraftId,
            DepartureAirportId = command.DepartureAirportId,
            ArriveAirportId = command.ArriveAirportId,
            DurationMinutes = command.DurationMinutes,
            Price = command.Price,
            DepartureDate = command.DepartureDate,
            ArriveDate = command.ArriveDate,
            FlightDate = command.FlightDate,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(flight);

        return (
            TypedResults.Created($"/api/flights/{flight.Id}", flight),
            new FlightCreated(flight.Id, flight.FlightNumber, flight.Price, flight.FlightDate));
    }
}

public static class GetFlightsEndpoint
{
    [WolverineGet("/api/flights")]
    public static Task<IReadOnlyList<Flight>> Get(IQuerySession session, CancellationToken ct)
        => session.Query<Flight>().OrderBy(f => f.FlightDate).ToListAsync(ct);
}

public static class GetFlightByIdEndpoint
{
    [WolverineGet("/api/flights/{id}")]
    public static Flight? Get(Guid id, [Wolverine.Persistence.Entity] Flight? flight) => flight;
}
