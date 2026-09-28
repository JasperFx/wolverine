namespace Wolverine.Persistence.Durability.ScheduledMessageManagement;

public class ScheduledMessageSummary
{
    public Guid Id { get; set; }
    public string? MessageType { get; set; }
    public DateTimeOffset? ScheduledTime { get; set; }
    public string? Destination { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public int Attempts { get; set; }

    /// <summary>
    /// The tenant the message was scheduled for, read from the stored envelope. Null for a message
    /// scheduled without a tenant, or when the stored body cannot be read.
    /// </summary>
    public string? TenantId { get; set; }
}
