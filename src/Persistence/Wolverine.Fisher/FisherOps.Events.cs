using Fisher;
using Fisher.Events;
using JasperFx.Core;

namespace Wolverine.Fisher;

/// <summary>
/// Event store side effects beyond starting a new stream: appending to a stream that already
/// exists, and archiving one.
/// </summary>
/// <remarks>
/// <para>
/// Appending to the stream the handler is <em>already</em> working on belongs in the aggregate
/// handler workflow ([WriteAggregate] / IEventStream&lt;T&gt; / the Events return type), which also
/// gives you the aggregate state and its concurrency protection. These side effects are for the
/// other case: touching some <em>other</em> stream from a handler that has no aggregate of its own.
/// </para>
/// <para>
/// Fisher has no <c>UnArchiveStream</c> or <c>TombstoneStream</c> — those are Polecat's own
/// archive-lifecycle extras — so there is no op for either here.
/// </para>
/// </remarks>
public static partial class FisherOps
{
    /// <summary>
    /// Return a side effect of appending events to an existing event stream by Guid identity
    /// </summary>
    public static AppendToStream Append(Guid streamId, params object[] events) => new(streamId, events);

    /// <summary>
    /// Return a side effect of appending events to an existing event stream by string key
    /// </summary>
    public static AppendToStream Append(string streamKey, params object[] events) => new(streamKey, events);

    /// <summary>
    /// Return a side effect of appending events to an existing event stream by Guid identity,
    /// asserting that the stream is at the supplied version or the transaction is aborted with
    /// a ConcurrencyException
    /// </summary>
    public static AppendToStream Append(Guid streamId, long expectedVersion, params object[] events)
        => new(streamId, events) { ExpectedVersion = expectedVersion };

    /// <summary>
    /// Return a side effect of appending events to an existing event stream by string key,
    /// asserting that the stream is at the supplied version or the transaction is aborted with
    /// a ConcurrencyException
    /// </summary>
    public static AppendToStream Append(string streamKey, long expectedVersion, params object[] events)
        => new(streamKey, events) { ExpectedVersion = expectedVersion };

    /// <summary>
    /// Return a side effect of archiving an event stream by Guid identity
    /// </summary>
    public static ArchiveStream ArchiveStream(Guid streamId) => new(streamId);

    /// <summary>
    /// Return a side effect of archiving an event stream by string key
    /// </summary>
    public static ArchiveStream ArchiveStream(string streamKey) => new(streamKey);
}

/// <summary>
/// Shared identity handling for the stream side effects: exactly one of a Guid id or a string key,
/// neither of which may be the empty value.
/// </summary>
/// <remarks>
/// The Guid/string discrimination is on <c>StreamId == Guid.Empty</c>, the same sentinel
/// <c>StartStream&lt;T&gt;</c> uses, so an empty identity would silently take the string branch and
/// archive nothing. Both constructors refuse it.
/// </remarks>
public abstract class StreamOp : ITenantedFisherOp
{
    protected StreamOp(Guid streamId)
    {
        if (streamId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(streamId), "The stream id cannot be Guid.Empty");
        }

        StreamId = streamId;
    }

    protected StreamOp(string streamKey)
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

    /// <summary>
    /// The event operations to write through, scoped to the tenant when one is set. Fisher's
    /// IDocumentOperations does not carry the write-side Events surface, so this resolves through
    /// ITenantOperations / IDocumentSession rather than through IDocumentOperations.
    /// </summary>
    protected EventOperations ResolveEvents(IDocumentSession session)
        => TenantId != null ? session.ForTenant(TenantId).Events : session.Events;

    public abstract void Execute(IDocumentSession session);
}

public class AppendToStream : StreamOp
{
    public AppendToStream(Guid streamId, params object[] events) : base(streamId)
    {
        Events.AddRange(events);
    }

    public AppendToStream(string streamKey, params object[] events) : base(streamKey)
    {
        Events.AddRange(events);
    }

    /// <summary>
    /// Optional optimistic concurrency check. When set, Fisher will abort the transaction if
    /// the stream is not at this version
    /// </summary>
    public long? ExpectedVersion { get; set; }

    public List<object> Events { get; } = new();

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

    public override void Execute(IDocumentSession session)
    {
        var target = ResolveEvents(session);

        if (StreamId == Guid.Empty)
        {
            if (ExpectedVersion.HasValue)
            {
                target.Append(StreamKey, ExpectedVersion.Value, Events.ToArray());
            }
            else
            {
                target.Append(StreamKey, Events.ToArray());
            }
        }
        else
        {
            if (ExpectedVersion.HasValue)
            {
                target.Append(StreamId, ExpectedVersion.Value, Events.ToArray());
            }
            else
            {
                target.Append(StreamId, Events.ToArray());
            }
        }
    }
}

public class ArchiveStream : StreamOp
{
    public ArchiveStream(Guid streamId) : base(streamId) { }

    public ArchiveStream(string streamKey) : base(streamKey) { }

    public override void Execute(IDocumentSession session)
    {
        var target = ResolveEvents(session);

        if (StreamId == Guid.Empty)
        {
            target.ArchiveStream(StreamKey);
        }
        else
        {
            target.ArchiveStream(StreamId);
        }
    }
}
