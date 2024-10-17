using JasperFx.Core;
using Marten;
using MassTransit;

namespace Wolverine.Marten;

#region sample_IMartenOp

/// <summary>
/// Interface for any kind of Marten related side effect
/// </summary>
public interface IMartenOp : ISideEffect
{
    void Execute(IDocumentSession session);
}


#endregion

/// <summary>
/// Access to Marten related side effect return values from message handlers
/// </summary>
public static class MartenOps
{
    /// <summary>
    /// Return a side effect of storing the specified document in Marten
    /// </summary>
    /// <param name="document"></param>
    /// <param name="tenantId"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static StoreDoc<T> Store<T>(T document, string? tenantId = null) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new StoreDoc<T>(document, tenantId);
    }

    /// <summary>
    /// Return a side effect of inserting the specified document in Marten
    /// </summary>
    /// <param name="document"></param>
    /// <param name="tenantId"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static InsertDoc<T> Insert<T>(T document, string? tenantId = null) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new InsertDoc<T>(document, tenantId);
    }

    /// <summary>
    /// Return a side effect of updating the specified document in Marten
    /// </summary>
    /// <param name="document"></param>
    /// <param name="tenantId"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static UpdateDoc<T> Update<T>(T document, string? tenantId = null) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new UpdateDoc<T>(document, tenantId);
    }

    /// <summary>
    /// Return a side effect of deleting the specified document in Marten
    /// </summary>
    /// <param name="document"></param>
    /// <param name="tenantId"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static DeleteDoc<T> Delete<T>(T document, string? tenantId = null) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new DeleteDoc<T>(document, tenantId);
    }

    /// <summary>
    /// Return a side effect of starting a new event stream in Marten
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="events"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static StartStream<T> StartStream<T>(Guid streamId, params object[] events) where T : class
    {
        return new StartStream<T>(streamId, null, events);
    }

    /// <summary>
    /// Return a side effect of starting a new event stream in Marten
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="tenantId"></param>
    /// <param name="events"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static StartStream<T> StartStream<T>(Guid streamId, string? tenantId, params object[] events) where T : class
    {
        return new StartStream<T>(streamId, tenantId, events);
    }

    /// <summary>
    /// Return a side effect of starting a new event stream in Marten. This overload
    /// creates a sequential Guid for the new stream that can be accessed from the
    /// return value
    /// </summary>
    /// <param name="events"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IStartStream StartStream<T>(params object[] events) where T : class
    {
        var streamId = CombGuidIdGeneration.NewGuid();
        return new StartStream<T>(streamId, null, events);
    }

    /// <summary>
    /// Return a side effect of starting a new event stream in Marten
    /// </summary>
    /// <param name="streamKey"></param>
    /// <param name="events"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IStartStream StartStream<T>(string streamKey, params object[] events) where T : class
    {
        return new StartStream<T>(streamKey, null, events);
    }
    
    /// <summary>
    /// Return a side effect of starting a new event stream in Marten
    /// </summary>
    /// <param name="streamKey"></param>
    /// <param name="tenantId"></param>
    /// <param name="events"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IStartStream StartStream<T>(string streamKey, string? tenantId, params object[] events) where T : class
    {
        return new StartStream<T>(streamKey, tenantId, events);
    }

    /// <summary>
    /// As it says, do nothing
    /// </summary>
    /// <returns></returns>
    public static NoOp Nothing() => new NoOp();
}

/// <summary>
/// Represents a "do nothing" action in cases where you do not need
/// to make any Marten action
/// </summary>
public class NoOp : IMartenOp
{
    public void Execute(IDocumentSession session)
    {
        // nothing
    }
}

public interface IStartStream : IMartenOp
{
    string StreamKey { get; }
    Guid StreamId { get; }

    Type AggregateType { get; }

    IReadOnlyList<object> Events { get; }
    string? TenantId { get; }
}

public class StartStream<T> : IStartStream where T : class
{
    public string StreamKey { get; } = string.Empty;
    public Guid StreamId { get; }
    public string? TenantId { get; }

    public StartStream(Guid streamId, string? tenantId, params object[] events)
    {
        StreamId = streamId;
        TenantId = tenantId;
        Events.AddRange(events);
    }

    public StartStream(string streamKey, string? tenantId, params object[] events)
    {
        StreamKey = streamKey;
        TenantId = tenantId;
        Events.AddRange(events);
    }
    
    public StartStream(Guid streamId, string? tenantId, IList<object> events)
    {
        StreamId = streamId;
        TenantId = tenantId;
        Events.AddRange(events);
    }

    public StartStream(string streamKey, string? tenantId, IList<object> events)
    {
        StreamKey = streamKey;
        TenantId = tenantId;
        Events.AddRange(events);
    }

    public StartStream<T> With(object[] events)
    {
        Events.AddRange(events);
        return this;
    }

    public StartStream<T> With(object @event)
    {
        Events.Add(@event);
        return this;
    }

    public List<object> Events { get; } = new();


    public void Execute(IDocumentSession session)
    {
        if (StreamId == Guid.Empty)
        {
            if (StreamKey.IsNotEmpty())
            {
                session.ForTenant(TenantId ?? session.TenantId).Events.StartStream<T>(StreamKey, Events.ToArray());
            }
            else
            {
                session.ForTenant(TenantId ?? session.TenantId).Events.StartStream<T>(Events.ToArray());
            }
        }
        else
        {
            session.ForTenant(TenantId ?? session.TenantId).Events.StartStream<T>(StreamId, Events.ToArray());
        }
    }

    Type IStartStream.AggregateType => typeof(T);

    IReadOnlyList<object> IStartStream.Events => Events;
}

public class StoreDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public StoreDoc(T document, string? tenantId = null) : base(document, tenantId)
    {
        _document = document;
    }

    public override void Execute(IDocumentSession session)
    {
        if (TenantId is not null)
            session.ForTenant(TenantId).Store(_document);
        else
            session.Store(_document);
    }
}

public class InsertDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public InsertDoc(T document, string? tenantId = null) : base(document, tenantId)
    {
        _document = document;
    }

    public override void Execute(IDocumentSession session)
    {
        if (TenantId is not null)
            session.ForTenant(TenantId).Insert(_document);
        else
            session.Insert(_document);
    }
}

public class UpdateDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public UpdateDoc(T document, string? tenantId = null) : base(document, tenantId)
    {
        _document = document;
    }

    public override void Execute(IDocumentSession session)
    {
        if (TenantId is not null)
            session.ForTenant(TenantId).Update(_document);
        else
            session.Update(_document);
    }
}

public class DeleteDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public DeleteDoc(T document, string? tenantId = null) : base(document, tenantId)
    {
        _document = document;
    }

    public override void Execute(IDocumentSession session)
    {
        if (TenantId is not null)
            session.ForTenant(TenantId).Delete(_document);
        else
            session.Delete(_document);
    }
}

public abstract class DocumentOp : IMartenOp
{
    public object Document { get; }
    public string? TenantId { get; }

    protected DocumentOp(object document, string? tenantId = null)
    {
        Document = document;
        TenantId = tenantId;
    }

    public abstract void Execute(IDocumentSession session);
}