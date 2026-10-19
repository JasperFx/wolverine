using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace OutboxDemo;

public record SubmitRegistration(string EventId, string MemberId, decimal Payment);

public static class SubmitRegistrationEndpoint
{
    // Sad path: duplicate registration check
    public static async Task<ProblemDetails> ValidateAsync(
        SubmitRegistration command,
        IQuerySession session)
    {
        var duplicate = await session.Query<Registration>()
            .AnyAsync(r => r.MemberId == command.MemberId && r.EventId == command.EventId);

        if (duplicate)
            return new ProblemDetails
            {
                Detail = $"Duplicate registration for member {command.MemberId} at event {command.EventId}",
                Status = 409,
            };

        return WolverineContinue.NoProblems;
    }

    // Return Results.NoContent() as the HTTP response.
    // Registration (Saga) and RegistrationSubmitted are cascading messages —
    // Wolverine auto-persists the saga and publishes via the outbox.
    [WolverinePost("/registration")]
    public static (IResult, Registration, RegistrationSubmitted) Post(
        SubmitRegistration command)
    {
        var registration = new Registration
        {
            Id = Guid.NewGuid(),
            RegistrationDate = DateTime.UtcNow,
            MemberId = command.MemberId,
            EventId = command.EventId,
            Payment = command.Payment,
        };

        return (
            Results.NoContent(),
            registration,
            new RegistrationSubmitted(
                registration.Id,
                registration.RegistrationDate,
                registration.MemberId,
                registration.EventId,
                registration.Payment)
        );
    }
}
