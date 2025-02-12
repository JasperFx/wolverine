namespace Wolverine.Persistence.Durability.DeadLetterManagement;

public class DeadLetterEnvelopeResults
{
    public int TotalCount { get; set; }
    public List<DeadLetterEnvelope> Envelopes { get; set; } = new();
    public int PageNumber { get; set; }
}