using Marten;
using Marten.Schema.Identity;

namespace Wolverine.Marten;

/// <summary>
/// Interface for any kind of Marten related side effect
/// </summary>
public interface IMartenOp : ISideEffect
{
    void Execute(IDocumentSession session);
}

/// <summary>
/// Access to Marten related side effect return values from message handlers
/// </summary>
public static class MartenOps
{
    /// <summary>
    /// Return a side effect of storing the specified document in Marten 
    /// </summary>
    /// <param name="document"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static StoreDoc<T> Store<T>(T document) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new StoreDoc<T>(document);
    }

    /// <summary>
    /// Return a side effect of inserting the specified document in Marten 
    /// </summary>
    /// <param name="document"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static InsertDoc<T> Insert<T>(T document) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new InsertDoc<T>(document);
    }

    /// <summary>
    /// Return a side effect of updating the specified document in Marten 
    /// </summary>
    /// <param name="document"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static UpdateDoc<T> Update<T>(T document) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new UpdateDoc<T>(document);
    }
    
    /// <summary>
    /// Return a side effect of deleting the specified document in Marten 
    /// </summary>
    /// <param name="document"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static DeleteDoc<T> Delete<T>(T document) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new DeleteDoc<T>(document);
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
        return new StartStream<T>(streamId, events);
    }
    
    /// <summary>
    /// Return a side effect of starting a new event stream in Marten. This overload
    /// creates a sequential Guid for the new stream that can be accessed from the
    /// return value
    /// </summary>
    /// <param name="events"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static StartStream StartStream<T>(params object[] events) where T : class
    {
        var streamId = CombGuidIdGeneration.NewGuid();
        return new StartStream<T>(streamId, events);
    }
    
    /// <summary>
    /// Return a side effect of starting a new event stream in Marten 
    /// </summary>
    /// <param name="streamKey"></param>
    /// <param name="events"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static StartStream StartStream<T>(string streamKey, params object[] events) where T : class
    {
        return new StartStream<T>(streamKey, events);
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

public interface StartStream : IMartenOp
{
    string StreamKey { get; }
    Guid StreamId { get; }
    
    Type AggregateType { get; }
    
    IReadOnlyList<object> Events { get; }
}

// TODO -- try to eliminate the generic. Will need Marten changes to do so
public class StartStream<T> : StartStream where T : class
{
    public string StreamKey { get; }
    public Guid StreamId { get; }

    public StartStream(Guid streamId, params object[] events)
    {
        StreamId = streamId;
        Events.AddRange(events);
    }
    
    public StartStream(string streamKey, params object[] events)
    {
        StreamKey = streamKey;
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
            session.Events.StartStream<T>(StreamKey, Events.ToArray());
        }
        else
        {
            session.Events.StartStream<T>(StreamId, Events.ToArray());
        }
    }

    Type StartStream.AggregateType => typeof(T);

    IReadOnlyList<object> StartStream.Events => Events;
}



public class StoreDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public StoreDoc(T document) : base(document)
    {
        _document = document;
    }

    public override void Execute(IDocumentSession session)
    {
        session.Store(_document);
    }
}

public class InsertDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public InsertDoc(T document) : base(document)
    {
        _document = document;
    }

    public override void Execute(IDocumentSession session)
    {
        session.Insert(_document);
    }
}

public class UpdateDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public UpdateDoc(T document) : base(document)
    {
        _document = document;
    }

    public override void Execute(IDocumentSession session)
    {
        session.Update(_document);
    }
}

public class DeleteDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public DeleteDoc(T document) : base(document)
    {
        _document = document;
    }

    public override void Execute(IDocumentSession session)
    {
        session.Delete(_document);
    }
}

public abstract class DocumentOp : IMartenOp
{
    public object Document { get; }

    protected DocumentOp(object document)
    {
        Document = document;
    }

    public abstract void Execute(IDocumentSession session);
}

