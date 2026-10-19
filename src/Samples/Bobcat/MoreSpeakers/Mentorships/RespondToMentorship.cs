using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Mentorships;

public record AcceptMentorship(Guid MentorshipId, string? ResponseMessage);

public static class AcceptMentorshipEndpoint
{
    public static ProblemDetails Validate([Entity("MentorshipId")] Mentorship? mentorship)
    {
        if (mentorship is null)
            return new ProblemDetails { Detail = "Mentorship not found", Status = 404 };
        if (mentorship.Status != MentorshipStatus.Pending)
            return new ProblemDetails { Detail = $"Cannot accept a mentorship in '{mentorship.Status}' status", Status = 400 };
        return WolverineContinue.NoProblems;
    }

    [WolverinePost("/api/mentorships/{mentorshipId}/accept")]
    public static Mentorship Post(
        AcceptMentorship command,
        [Entity("MentorshipId", Required = true)] Mentorship mentorship,
        IDocumentSession session)
    {
        mentorship.Status = MentorshipStatus.Active;
        mentorship.ResponseMessage = command.ResponseMessage;
        mentorship.RespondedAt = DateTimeOffset.UtcNow;
        mentorship.StartedAt = DateTimeOffset.UtcNow;

        session.Store(mentorship);
        return mentorship;
    }
}

public record DeclineMentorship(Guid MentorshipId, string? ResponseMessage);

public static class DeclineMentorshipEndpoint
{
    public static ProblemDetails Validate([Entity("MentorshipId")] Mentorship? mentorship)
    {
        if (mentorship is null)
            return new ProblemDetails { Detail = "Mentorship not found", Status = 404 };
        if (mentorship.Status != MentorshipStatus.Pending)
            return new ProblemDetails { Detail = $"Cannot decline a mentorship in '{mentorship.Status}' status", Status = 400 };
        return WolverineContinue.NoProblems;
    }

    [WolverinePost("/api/mentorships/{mentorshipId}/decline")]
    public static Mentorship Post(
        DeclineMentorship command,
        [Entity("MentorshipId", Required = true)] Mentorship mentorship,
        IDocumentSession session)
    {
        mentorship.Status = MentorshipStatus.Declined;
        mentorship.ResponseMessage = command.ResponseMessage;
        mentorship.RespondedAt = DateTimeOffset.UtcNow;

        session.Store(mentorship);
        return mentorship;
    }
}

public record CompleteMentorship(Guid MentorshipId);

public static class CompleteMentorshipEndpoint
{
    public static ProblemDetails Validate([Entity("MentorshipId")] Mentorship? mentorship)
    {
        if (mentorship is null)
            return new ProblemDetails { Detail = "Mentorship not found", Status = 404 };
        if (mentorship.Status != MentorshipStatus.Active)
            return new ProblemDetails { Detail = "Only active mentorships can be completed", Status = 400 };
        return WolverineContinue.NoProblems;
    }

    [WolverinePost("/api/mentorships/{mentorshipId}/complete")]
    public static Mentorship Post(
        CompleteMentorship command,
        [Entity("MentorshipId", Required = true)] Mentorship mentorship,
        IDocumentSession session)
    {
        mentorship.Status = MentorshipStatus.Completed;
        mentorship.CompletedAt = DateTimeOffset.UtcNow;

        session.Store(mentorship);
        return mentorship;
    }
}
