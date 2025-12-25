using NATS.Client.JetStream.Models;

namespace Wolverine.Nats.Configuration;

public class StreamConfiguration
{
    public string Name { get; set; } = string.Empty;
    public List<string> Subjects { get; set; } = new();
    public StreamConfigRetention Retention { get; set; } = StreamConfigRetention.Limits;
    public StreamConfigStorage Storage { get; set; } = StreamConfigStorage.File;
    public int? MaxMessages { get; set; }
    public long? MaxBytes { get; set; }
    public TimeSpan? MaxAge { get; set; }
    public int? MaxMessagesPerSubject { get; set; }
    public StreamConfigDiscard DiscardPolicy { get; set; } = StreamConfigDiscard.Old;
    public int Replicas { get; set; } = 1;
    public bool AllowRollup { get; set; }
    public bool AllowDirect { get; set; }
    public bool DenyDelete { get; set; }
    public bool DenyPurge { get; set; }

    /// <summary>
    /// Add a subject to this stream
    /// </summary>
    public StreamConfiguration WithSubject(string subject)
    {
        if (!Subjects.Contains(subject))
        {
            Subjects.Add(subject);
        }
        return this;
    }

    /// <summary>
    /// Add multiple subjects to this stream
    /// </summary>
    public StreamConfiguration WithSubjects(params string[] subjects)
    {
        foreach (var subject in subjects)
        {
            WithSubject(subject);
        }
        return this;
    }

    /// <summary>
    /// Configure retention limits
    /// </summary>
    public StreamConfiguration WithLimits(
        int? maxMessages = null,
        long? maxBytes = null,
        TimeSpan? maxAge = null
    )
    {
        Retention = StreamConfigRetention.Limits;
        if (maxMessages.HasValue)
        {
            MaxMessages = maxMessages;
        }

        if (maxBytes.HasValue)
        {
            MaxBytes = maxBytes;
        }

        if (maxAge.HasValue)
        {
            MaxAge = maxAge;
        }

        return this;
    }

    /// <summary>
    /// Configure as work queue (retention by interest)
    /// </summary>
    public StreamConfiguration AsWorkQueue()
    {
        Retention = StreamConfigRetention.Interest;
        return this;
    }

    /// <summary>
    /// Configure for high availability
    /// </summary>
    public StreamConfiguration WithReplicas(int replicas)
    {
        Replicas = replicas;
        return this;
    }
}
