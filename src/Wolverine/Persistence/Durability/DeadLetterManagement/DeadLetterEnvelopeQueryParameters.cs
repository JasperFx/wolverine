namespace Wolverine.Persistence.Durability.DeadLetterManagement;

public class DeadLetterEnvelopeQueryParameters
{
    public uint Limit { get; set; } = 100;
    public Guid? StartId { get; set; }
    public string? MessageType { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? Until { get; set; }
}