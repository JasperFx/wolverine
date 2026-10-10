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
    /// Deduplication window for this stream. Within this window JetStream discards messages carrying a
    /// duplicate <c>Nats-Msg-Id</c>. When null the transport-wide
    /// <see cref="JetStreamDefaults.DuplicateWindow"/> is applied.
    /// </summary>
    public TimeSpan? DuplicateWindow { get; set; }
    
    /// <summary>
    /// Enable scheduled message delivery (requires NATS Server 2.12+)
    /// Once enabled on a stream, this cannot be disabled.
    /// </summary>
    public bool AllowMsgSchedules { get; set; }

    /// <summary>
    /// GH-4860. Make this stream a mirror of another stream. A mirror stream has no subjects of its own
    /// and replicates every message of its origin, so <see cref="Subjects"/> is left empty and the
    /// origin's name, optional filter, start position and domain come from the <see cref="StreamSource"/>.
    /// Null (the default) means the stream is not a mirror, and whatever mirror an existing stream has on
    /// the server is left alone.
    /// </summary>
    public StreamSource? Mirror { get; set; }

    /// <summary>
    /// GH-4860. Streams this stream aggregates messages from, in addition to its own subjects. Empty (the
    /// default) means the sources of an existing stream on the server are left alone.
    /// </summary>
    public List<StreamSource> Sources { get; set; } = new();

    /// <summary>
    /// GH-4860. Where the server places this stream: a cluster name and/or the tags a server must carry
    /// to hold it. Null (the default) leaves placement to the server, and leaves an existing stream's
    /// placement alone.
    /// </summary>
    public Placement? Placement { get; set; }

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
    /// Sets <see cref="Retention"/> to <see cref="StreamConfigRetention.Interest"/> -- despite the name, not
    /// JetStream's work-queue retention. An interest stream keeps a message only while a consumer is interested
    /// in it, so a message published while no consumer is bound is discarded on arrival. For JetStream's
    /// work-queue retention, which keeps every message until it is acknowledged, set
    /// <c>Retention = StreamConfigRetention.Workqueue</c> instead.
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

    /// <summary>
    /// Set the deduplication window for this stream (see <see cref="DuplicateWindow"/>).
    /// </summary>
    public StreamConfiguration WithDeduplicationWindow(TimeSpan window)
    {
        DuplicateWindow = window;
        return this;
    }

    /// <summary>
    /// Enable scheduled message delivery (requires NATS Server 2.12+).
    /// Once enabled on a stream, this cannot be disabled.
    /// </summary>
    public StreamConfiguration EnableScheduledDelivery()
    {
        AllowMsgSchedules = true;
        return this;
    }

    /// <summary>
    /// Make this stream a mirror of <paramref name="originStream"/> (see <see cref="Mirror"/>). The
    /// optional callback refines the origin: a filter subject, a start sequence or time, a JetStream domain
    /// </summary>
    public StreamConfiguration MirrorOf(string originStream, Action<StreamSource>? configure = null)
    {
        var source = new StreamSource { Name = originStream };
        configure?.Invoke(source);
        Mirror = source;
        return this;
    }

    /// <summary>
    /// Aggregate messages from <paramref name="originStream"/> into this stream (see <see cref="Sources"/>).
    /// Call once per origin; the optional callback refines it the same way <see cref="MirrorOf"/>'s does
    /// </summary>
    public StreamConfiguration SourcedFrom(string originStream, Action<StreamSource>? configure = null)
    {
        var source = new StreamSource { Name = originStream };
        configure?.Invoke(source);
        Sources.Add(source);
        return this;
    }

    /// <summary>
    /// Place this stream on servers of <paramref name="cluster"/> and/or carrying every one of
    /// <paramref name="tags"/> (see <see cref="Placement"/>)
    /// </summary>
    public StreamConfiguration PlacedOn(string? cluster, params string[] tags)
    {
        Placement = new Placement { Cluster = cluster, Tags = tags.Length == 0 ? null : tags.ToList() };
        return this;
    }
}
