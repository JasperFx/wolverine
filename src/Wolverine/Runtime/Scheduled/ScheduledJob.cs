using System;

namespace Wolverine.Runtime.Scheduled;

public class ScheduledJob
{
    public ScheduledJob(Guid envelopeId)
    {
        EnvelopeId = envelopeId;
    }

    public Guid EnvelopeId { get; }

    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ExecutionTime { get; set; }

    public string? MessageType { get; set; }
}
