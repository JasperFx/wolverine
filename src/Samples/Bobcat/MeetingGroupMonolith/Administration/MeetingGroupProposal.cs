namespace Administration;

public class MeetingGroupProposal
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string LocationCity { get; set; } = string.Empty;
    public string LocationCountryCode { get; set; } = string.Empty;
    public Guid ProposalUserId { get; set; }
    public ProposalStatus Status { get; set; } = ProposalStatus.InVerification;
    public string? DecisionReason { get; set; }
    public DateTimeOffset ProposalDate { get; set; }
    public DateTimeOffset? DecisionDate { get; set; }
}

public enum ProposalStatus
{
    InVerification,
    Accepted,
    Rejected,
}
