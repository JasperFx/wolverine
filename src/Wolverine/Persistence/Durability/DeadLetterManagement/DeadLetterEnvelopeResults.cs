namespace Wolverine.Persistence.Durability.DeadLetterManagement;

public class DeadLetterEnvelopeResults
{
    public int TotalCount { get; set; }
    public List<DeadLetterEnvelope> Envelopes { get; set; } = new();

    public int PageNumber { get; set; }
    
    public Uri? DatabaseUri { get; set; }
}

public class DeadLetterEnvelopeGetRequest
{
    /// <summary>
    /// Number of records to return per page.
    /// </summary>
    public uint Limit { get; set; } = 100;

    public int PageNumber { get; set; }
    
    public string? MessageType { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }

    /// <summary>
    /// When set, restricts results to envelopes whose replayable flag matches. <c>null</c>
    /// (the default) keeps the current behavior of not filtering on the replayable state.
    /// <c>false</c> returns only envelopes not yet marked replayable; <c>true</c> returns only
    /// those already marked for replay.
    /// </summary>
    public bool? Replayable { get; set; }

    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? Until { get; set; }
    public string? TenantId { get; set; }
    
    public Uri? DatabaseUri { get; set; }
}