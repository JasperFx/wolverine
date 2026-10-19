using FluentValidation;
using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Speakers;

public record UpdateSpeakerProfile(
    Guid SpeakerId,
    string FirstName,
    string LastName,
    string? Bio,
    string? Goals,
    string? HeadshotUrl,
    string? SessionizeUrl,
    bool IsAvailableForMentoring,
    int MaxMentees,
    string? MentorshipFocus,
    List<string>? Expertise,
    List<SocialLink>? SocialLinks
)
{
    public class Validator : AbstractValidator<UpdateSpeakerProfile>
    {
        public Validator()
        {
            RuleFor(x => x.FirstName).NotEmpty();
            RuleFor(x => x.LastName).NotEmpty();
            RuleFor(x => x.MaxMentees).GreaterThanOrEqualTo(0);
        }
    }
}

public static class UpdateSpeakerProfileEndpoint
{
    [WolverinePut("/api/speakers/{speakerId}")]
    public static Speaker Put(
        UpdateSpeakerProfile command,
        [Entity("SpeakerId", Required = true)] Speaker speaker,
        IDocumentSession session)
    {
        speaker.FirstName = command.FirstName;
        speaker.LastName = command.LastName;
        speaker.Bio = command.Bio;
        speaker.Goals = command.Goals;
        speaker.HeadshotUrl = command.HeadshotUrl;
        speaker.SessionizeUrl = command.SessionizeUrl;
        speaker.IsAvailableForMentoring = command.IsAvailableForMentoring;
        speaker.MaxMentees = command.MaxMentees;
        speaker.MentorshipFocus = command.MentorshipFocus;
        speaker.Expertise = command.Expertise ?? [];
        speaker.SocialLinks = command.SocialLinks ?? [];
        speaker.UpdatedAt = DateTimeOffset.UtcNow;

        session.Store(speaker);
        return speaker;
    }
}
