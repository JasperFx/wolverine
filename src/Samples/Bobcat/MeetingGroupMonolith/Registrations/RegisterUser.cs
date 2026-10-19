using FluentValidation;
using Marten;
using MeetingGroupMonolith;
using UserAccess;
using Wolverine.Http;

namespace Registrations;

public record RegisterUser(string Login, string Email, string FirstName, string LastName, string Password)
{
    public class Validator : AbstractValidator<RegisterUser>
    {
        public Validator()
        {
            RuleFor(x => x.Login).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Email).NotEmpty().EmailAddress();
            RuleFor(x => x.FirstName).NotEmpty();
            RuleFor(x => x.LastName).NotEmpty();
            RuleFor(x => x.Password).NotEmpty().MinimumLength(8);
        }
    }
}

public static class RegisterUserEndpoint
{
    // Cascading message: create the User and publish NewUserRegisteredEvent
    // to the durable local queue so the Meetings module can create a Member
    [WolverinePost("/api/registrations")]
    public static (User, NewUserRegisteredEvent) Post(RegisterUser command, IDocumentSession session)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Login = command.Login,
            Email = command.Email,
            FirstName = command.FirstName,
            LastName = command.LastName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(user);

        return (user, new NewUserRegisteredEvent(user.Id, user.Login, user.Email, user.FirstName, user.LastName));
    }
}
