namespace Mentorships;

public class Mentorship
{
    public Guid Id { get; set; }
    public Guid MentorId { get; set; }
    public string MentorName { get; set; } = string.Empty;
    public Guid MenteeId { get; set; }
    public string MenteeName { get; set; } = string.Empty;
    public MentorshipStatus Status { get; set; } = MentorshipStatus.Pending;
    public MentorshipType Type { get; set; }
    public List<string> FocusAreas { get; set; } = [];
    public string? RequestMessage { get; set; }
    public string? ResponseMessage { get; set; }
    public string? Notes { get; set; }
    public string? PreferredFrequency { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum MentorshipStatus { Pending, Accepted, Active, Completed, Cancelled, Declined }
public enum MentorshipType { NewToExperienced, ExperiencedToExperienced }
