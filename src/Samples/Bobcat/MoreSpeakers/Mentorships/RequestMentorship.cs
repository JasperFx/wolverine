using FluentValidation;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Speakers;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Mentorships;

public record RequestMentorship(
    Guid MentorId,
    Guid MenteeId,
    MentorshipType Type,
    string? RequestMessage,
    List<string>? FocusAreas,
    string? PreferredFrequency
)
{
    public class Validator : AbstractValidator<RequestMentorship>
    {
        public Validator()
        {
            RuleFor(x => x.MentorId).NotEmpty();
            RuleFor(x => x.MenteeId).NotEmpty();
            RuleFor(x => x.MentorId).NotEqual(x => x.MenteeId)
                .WithMessage("Cannot mentor yourself");
        }
    }
}

public static class RequestMentorshipEndpoint
{
    // Multiple [Entity] parameters — Wolverine batch-loads both in one round-trip
    public static ProblemDetails Validate(
        RequestMentorship command,
        [Entity("MentorId", OnMissing = OnMissing.ProblemDetailsWith400)] Speaker? mentor,
        [Entity("MenteeId", OnMissing = OnMissing.ProblemDetailsWith400)] Speaker? mentee)
    {
        if (mentor is not null && !mentor.IsAvailableForMentoring)
            return new ProblemDetails { Detail = "Mentor is not available for mentoring", Status = 400 };

        return WolverineContinue.NoProblems;
    }

    // Created rather than a bare Mentorship — see RegisterSpeakerEndpoint for why it is a
    // TypedResults value and not a (body, IResult) tuple.
    [WolverinePost("/api/mentorships")]
    public static Created<Mentorship> Post(
        RequestMentorship command,
        [Entity("MentorId", Required = true)] Speaker mentor,
        [Entity("MenteeId", Required = true)] Speaker mentee,
        IDocumentSession session)
    {
        var mentorship = new Mentorship
        {
            Id = Guid.NewGuid(),
            MentorId = mentor.Id,
            MentorName = mentor.FullName,
            MenteeId = mentee.Id,
            MenteeName = mentee.FullName,
            Status = MentorshipStatus.Pending,
            Type = command.Type,
            FocusAreas = command.FocusAreas ?? [],
            RequestMessage = command.RequestMessage,
            PreferredFrequency = command.PreferredFrequency,
            RequestedAt = DateTimeOffset.UtcNow,
        };

        session.Store(mentorship);
        return TypedResults.Created($"/api/mentorships/{mentorship.Id}", mentorship);
    }
}
