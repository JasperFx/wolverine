namespace Wolverine.Transports.Postgresql;

internal sealed class PostgresMessage
{
    public Guid Id { get; set; }
    
    public Guid SenderId { get; set; }

    public string? CorrelationId { get; set; }

    public string? MessageType { get; set; }

    public string? ContentType { get; set; }

    public DateTimeOffset ScheduledTime { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public byte[]? Data { get; set; }

    public int Attempts { get; set; }
}
