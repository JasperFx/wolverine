namespace Wolverine.Persistence.Durability.DeadLetterManagement;

public class DeadLetterEnvelopeQuery
{
    public DeadLetterEnvelopeQuery(TimeRange range)
    {
        Range = range;
    }

    public DeadLetterEnvelopeQuery(Guid[] messageIds)
    {
        MessageIds = messageIds;
    }

    public DeadLetterEnvelopeQuery()
    {
    }

    public int PageNumber { get; set; }
    public int PageSize { get; set; } = 100;

    public string? MessageType { get; set; }
    public string? ExceptionType { get; set; }
    public string? ReceivedAt { get; set; }
    
    public string? ExceptionMessage { get; set; }

    public TimeRange Range { get; set; } = TimeRange.AllTime();

    /// <summary>
    /// If set, this takes precedence over all other options
    /// </summary>
    public Guid[] MessageIds { get; set; } = [];
}