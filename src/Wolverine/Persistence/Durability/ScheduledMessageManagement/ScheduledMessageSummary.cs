namespace Wolverine.Persistence.Durability.ScheduledMessageManagement;

public class ScheduledMessageSummary
{
    public Guid Id { get; set; }
    public string? MessageType { get; set; }
    public DateTimeOffset? ScheduledTime { get; set; }
    public string? Destination { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public int Attempts { get; set; }
}
