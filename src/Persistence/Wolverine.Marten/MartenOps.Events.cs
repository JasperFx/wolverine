using JasperFx.Core;
using Marten;

namespace Wolverine.Marten;

/// <summary>
/// Event store side effects beyond starting a new stream: appending to a stream that already
/// exists, and archiving one.
/// </summary>
/// <remarks>
/// Appending to the stream the handler is *already* working on belongs in the aggregate handler
/// workflow ([WriteAggregate] / IEventStream&lt;T&gt; / the Events return type), which also gives
/// you the aggregate state and its concurrency protection. These side effects are for the other
/// case: touching some *other* stream from a handler that has no aggregate of its own.
/// </remarks>
public static partial class MartenOps
{
    /// <summary>
    /// Return a side effect of appending events to an existing event stream by Guid identity
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    public static AppendToStream Append(Guid streamId, params object[] events)
    {
        return new AppendToStream(streamId, events);
    }

    /// <summary>
    /// Return a side effect of appending events to an existing event stream by string key
    /// </summary>
    /// <param name="streamKey"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    public static AppendToStream Append(string streamKey, params object[] events)
    {
        return new AppendToStream(streamKey, events);
    }

    /// <summary>
    /// Return a side effect of appending events to an existing event stream by Guid identity,
    /// asserting that the stream is at the supplied version or the transaction is aborted with
    /// a ConcurrencyException
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="expectedVersion"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    public static AppendToStream Append(Guid streamId, long expectedVersion, params object[] events)
    {
        return new AppendToStream(streamId, events) { ExpectedVersion = expectedVersion };
    }

    /// <summary>
    /// Return a side effect of appending events to an existing event stream by string key,
    /// asserting that the stream is at the supplied version or the transaction is aborted with
    /// a ConcurrencyException
    /// </summary>
    /// <param name="streamKey"></param>
    /// <param name="expectedVersion"></param>
    /// <param name="events"></param>
    /// <returns></returns>
    public static AppendToStream Append(string streamKey, long expectedVersion, params object[] events)
    {
        return new AppendToStream(streamKey, events) { ExpectedVersion = expectedVersion };
    }

    /// <summary>
    /// Return a side effect of archiving an event stream by Guid identity
    /// </summary>
    /// <param name="streamId"></param>
    /// <returns></returns>
    public static ArchiveStream ArchiveStream(Guid streamId)
    {
        return new ArchiveStream(streamId);
    }

    /// <summary>
    /// Return a side effect of archiving an event stream by string key
    /// </summary>
    /// <param name="streamKey"></param>
    /// <returns></returns>
    public static ArchiveStream ArchiveStream(string streamKey)
    {
        return new ArchiveStream(streamKey);
    }
}

public class AppendToStream : ITenantedMartenOp
{
    public AppendToStream(Guid streamId, params object[] events)
    {
        if (streamId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(streamId), "The stream id cannot be Guid.Empty");
        }

        StreamId = streamId;
        Events.AddRange(events);
    }

    public AppendToStream(string streamKey, params object[] events)
    {
        if (streamKey.IsEmpty())
        {
            throw new ArgumentOutOfRangeException(nameof(streamKey), "The stream key cannot be null or empty");
        }

        StreamKey = streamKey;
        Events.AddRange(events);
    }

    public string StreamKey { get; } = string.Empty;

    public Guid StreamId { get; }

    /// <summary>
    /// Optional optimistic concurrency check. When set, Marten will abort the transaction if
    /// the stream is not at this version
    /// </summary>
    public long? ExpectedVersion { get; set; }

    public List<object> Events { get; } = new();

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant
    /// </summary>
    public string? TenantId { get; set; }

    public AppendToStream With(object @event)
    {
        Events.Add(@event);
        return this;
    }

    public AppendToStream With(object[] events)
    {
        Events.AddRange(events);
        return this;
    }

    public void Execute(IDocumentSession session)
    {
        IDocumentOperations target = TenantId != null ? session.ForTenant(TenantId) : session;

        if (StreamId == Guid.Empty)
        {
            if (ExpectedVersion.HasValue)
            {
                target.Events.Append(StreamKey, ExpectedVersion.Value, Events.ToArray());
            }
            else
            {
                target.Events.Append(StreamKey, Events.ToArray());
            }
        }
        else
        {
            if (ExpectedVersion.HasValue)
            {
                target.Events.Append(StreamId, ExpectedVersion.Value, Events.ToArray());
            }
            else
            {
                target.Events.Append(StreamId, Events.ToArray());
            }
        }
    }
}

public class ArchiveStream : ITenantedMartenOp
{
    public ArchiveStream(Guid streamId)
    {
        if (streamId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(streamId), "The stream id cannot be Guid.Empty");
        }

        StreamId = streamId;
    }

    public ArchiveStream(string streamKey)
    {
        if (streamKey.IsEmpty())
        {
            throw new ArgumentOutOfRangeException(nameof(streamKey), "The stream key cannot be null or empty");
        }

        StreamKey = streamKey;
    }

    public string StreamKey { get; } = string.Empty;

    public Guid StreamId { get; }

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant
    /// </summary>
    public string? TenantId { get; set; }

    public void Execute(IDocumentSession session)
    {
        IDocumentOperations target = TenantId != null ? session.ForTenant(TenantId) : session;

        if (StreamId == Guid.Empty)
        {
            target.Events.ArchiveStream(StreamKey);
        }
        else
        {
            target.Events.ArchiveStream(StreamId);
        }
    }
}
