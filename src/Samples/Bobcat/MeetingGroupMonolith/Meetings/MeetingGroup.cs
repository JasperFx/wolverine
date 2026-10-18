namespace Meetings;

public class MeetingGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string LocationCity { get; set; } = string.Empty;
    public string LocationCountryCode { get; set; } = string.Empty;
    public Guid CreatorId { get; set; }
    public List<MeetingGroupMember> Members { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public class MeetingGroupMember
{
    public Guid MemberId { get; set; }
    public string Role { get; set; } = "Member"; // Organizer, Member
    public DateTimeOffset JoinedAt { get; set; }
}

public class Meeting
{
    public Guid Id { get; set; }
    public Guid MeetingGroupId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime TermStartDate { get; set; }
    public DateTime TermEndDate { get; set; }
    public string LocationAddress { get; set; } = string.Empty;
    public int? AttendeesLimit { get; set; }
    public decimal Fee { get; set; }
    public List<MeetingAttendee> Attendees { get; set; } = [];
    public MeetingStatus Status { get; set; } = MeetingStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
}

public class MeetingAttendee
{
    public Guid MemberId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public bool FeePaid { get; set; }
}

public enum MeetingStatus { Active, Cancelled }

public class Member
{
    public Guid Id { get; set; }
    public string Login { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime? SubscriptionExpirationDate { get; set; }
}
