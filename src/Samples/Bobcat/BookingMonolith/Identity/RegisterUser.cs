using FluentValidation;
using BookingMonolith;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Identity;

public class UserAccount
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public record RegisterUser(string Email, string FirstName, string LastName, string Password)
{
    public class Validator : AbstractValidator<RegisterUser>
    {
        public Validator()
        {
            RuleFor(x => x.Email).NotEmpty().EmailAddress();
            RuleFor(x => x.FirstName).NotEmpty();
            RuleFor(x => x.LastName).NotEmpty();
            RuleFor(x => x.Password).NotEmpty().MinimumLength(8);
        }
    }
}

public static class RegisterUserEndpoint
{
    // Created rather than a bare UserAccount, so the response carries the 201 and Location a
    // newly-created resource should. By Wolverine.Http's tuple convention the first item is the
    // HTTP response — an IResult is executed as-is — and every item after it is a cascaded
    // message, which is how UserCreated reaches the Passenger module's durable local queue.
    [WolverinePost("/api/identity/register")]
    public static (Created<UserAccount>, UserCreated) Post(RegisterUser command, Marten.IDocumentSession session)
    {
        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Email = command.Email,
            FirstName = command.FirstName,
            LastName = command.LastName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(user);
        return (
            TypedResults.Created($"/api/identity/{user.Id}", user),
            new UserCreated(user.Id, user.Email, user.FirstName, user.LastName));
    }
}

/// <summary>
/// Reading a user back was missing entirely — the module could only be written to. Without it
/// the registration spec could assert nothing beyond "a Guid came back", and the Location header
/// the POST now emits would point at a route that did not exist.
/// </summary>
public static class GetUserEndpoint
{
    [WolverineGet("/api/identity/{id}")]
    public static UserAccount? Get(Guid id, [Entity] UserAccount? user) => user;
}
