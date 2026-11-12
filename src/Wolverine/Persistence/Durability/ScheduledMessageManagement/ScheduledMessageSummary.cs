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

    /// <summary>
    /// The saga the message was scheduled by, read from the stored envelope: a saga's cascaded
    /// timeouts carry its id. Null for a message scheduled outside a saga, or when the stored body
    /// cannot be read.
    /// </summary>
    public string? SagaId { get; set; }

    /// <summary>
    /// GH-5167. The <see cref="Envelope.GroupId"/> the message was scheduled with, such as a business
    /// identifier like <c>booking/{bookingId}</c>. Null when the message was scheduled without one.
    /// </summary>
    public string? GroupId { get; set; }
}
