using FluentValidation;
using Marten;
using Microsoft.AspNetCore.Mvc;
using PaymentsMonolith;
using Wolverine.Http;

namespace Users;

public record RegisterUser(string Email, string FullName, string Password)
{
    public class Validator : AbstractValidator<RegisterUser>
    {
        public Validator()
        {
            RuleFor(x => x.Email).NotEmpty().EmailAddress();
            RuleFor(x => x.FullName).NotEmpty();
            RuleFor(x => x.Password).NotEmpty().MinimumLength(8);
        }
    }
}

public static class RegisterUserEndpoint
{
    public static async Task<ProblemDetails> ValidateAsync(RegisterUser command, IQuerySession session)
    {
        var exists = await session.Query<User>().AnyAsync(u => u.Email == command.Email);
        if (exists)
            return new ProblemDetails { Detail = $"Email '{command.Email}' already registered", Status = 409 };

        return WolverineContinue.NoProblems;
    }

    [WolverinePost("/api/users")]
    public static (User, UserCreated) Post(RegisterUser command, IDocumentSession session)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = command.Email,
            FullName = command.FullName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(user);
        return (user, new UserCreated(user.Id, user.Email, user.FullName));
    }
}
