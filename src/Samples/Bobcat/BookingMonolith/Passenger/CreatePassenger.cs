using FluentValidation;
using Marten;
using BookingMonolith;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Passenger;

public record CreatePassenger(string Name, string PassportNumber, PassengerType Type, int Age)
{
    public class Validator : AbstractValidator<CreatePassenger>
    {
        public Validator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleFor(x => x.PassportNumber).NotEmpty();
        }
    }
}

public static class CreatePassengerEndpoint
{
    // First tuple item is the HTTP response (an IResult, executed as-is); the rest are cascaded
    // messages. See RegisterUserEndpoint.
    [WolverinePost("/api/passengers")]
    public static (Created<Passenger>, PassengerCreated) Post(CreatePassenger command, IDocumentSession session)
    {
        var passenger = new Passenger
        {
            Id = Guid.NewGuid(),
            Name = command.Name,
            PassportNumber = command.PassportNumber,
            Type = command.Type,
            Age = command.Age,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(passenger);
        return (
            TypedResults.Created($"/api/passengers/{passenger.Id}", passenger),
            new PassengerCreated(passenger.Id, passenger.Name));
    }
}

/// <summary>
/// Reading a passenger back was missing entirely — the module could only be written to, by the
/// POST above and by the UserCreated cascade below. Without it there is no way to observe that
/// registering a user really does create the passenger stub, which is the one place this
/// monolith's modules talk to each other.
/// </summary>
public static class GetPassengerByIdEndpoint
{
    [WolverineGet("/api/passengers/{id}")]
    public static Passenger? Get(Guid id, [Entity] Passenger? passenger) => passenger;
}

/// <summary>
/// Handles UserCreated from the Identity module — auto-creates a passenger stub.
/// Replaces: MassTransit IConsumer + RegisterNewUserHandler
/// </summary>
public static class UserCreatedHandler
{
    public static void Handle(UserCreated message, IDocumentSession session)
    {
        session.Store(new Passenger
        {
            Id = message.UserId,
            Name = $"{message.FirstName} {message.LastName}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }
}
