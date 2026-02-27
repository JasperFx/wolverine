namespace Wolverine.Persistence.Durability.ScheduledMessageManagement;

public class ScheduledMessageResults
{
    public int TotalCount { get; set; }
    public List<ScheduledMessageSummary> Messages { get; set; } = [];
    public int PageNumber { get; set; }
    public Uri? DatabaseUri { get; set; }
}
