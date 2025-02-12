namespace Wolverine.Persistence.Durability.DeadLetterManagement;

public class DeadLetterEnvelopeQuery
{
    public DeadLetterEnvelopeQuery(TimeRange range)
    {
        Range = range;
    }

    public int PageNumber { get; set; }
    public int PageSize { get; set; } = 100;
    public string? MessageType { get; set; }
    public string? ExceptionType { get; set; }
    public string? ReceivedAt { get; set; }
    public Uri? Database { get; set; }
    
    public TimeRange Range { get; set; }
}