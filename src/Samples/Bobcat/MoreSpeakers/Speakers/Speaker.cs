namespace Speakers;

/// <summary>
/// Speaker document stored in Marten. Replaces User (IdentityUser) + UserExpertise
/// join table + SpeakerType FK + UserSocialMediaSite table.
/// Marten stores expertise and social links as nested collections — no joins needed.
/// </summary>
public class Speaker
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName => $"{FirstName} {LastName}";
    public string? Bio { get; set; }
    public string? Goals { get; set; }
    public string? HeadshotUrl { get; set; }
    public string? SessionizeUrl { get; set; }
    public SpeakerType Type { get; set; } = SpeakerType.New;
    public List<string> Expertise { get; set; } = [];
    public List<SocialLink> SocialLinks { get; set; } = [];

    // Mentorship availability
    public bool IsAvailableForMentoring { get; set; }
    public int MaxMentees { get; set; }
    public string? MentorshipFocus { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public enum SpeakerType { New, Experienced }

public class SocialLink
{
    public string Platform { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
