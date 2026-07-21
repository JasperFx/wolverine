using JasperFx.Core;

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

    /// <summary>
    /// When set, restricts results to envelopes whose replayable flag matches. <c>null</c>
    /// (the default) keeps the current behavior of not filtering on the replayable column.
    /// <c>false</c> returns only envelopes not yet marked replayable; <c>true</c> returns only
    /// those already marked for replay.
    /// </summary>
    public bool? Replayable { get; set; }

    public TimeRange Range { get; set; } = TimeRange.AllTime();

    /// <summary>
    /// If set, this takes precedence over all other options
    /// </summary>
    public Guid[] MessageIds { get; set; } = [];

    /// <summary>
    /// Purely a marker for request/response scenarios
    /// </summary>
    public Guid QueryId { get; set; } = Guid.NewGuid();
}