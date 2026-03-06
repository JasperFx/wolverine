using System.Linq.Expressions;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Polecat.Events;
using Polecat;
using Wolverine.Configuration;
using Wolverine.Polecat.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Polecat;

/// <summary>
/// Interface for any kind of Polecat related side effect
/// </summary>
public interface IPolecatOp : ISideEffect
{
    void Execute(IDocumentSession session);
}

internal class PolecatOpPolicy : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            var candidates = chain.ReturnVariablesOfType<IEnumerable<IPolecatOp>>().ToArray();
            if (candidates.Any())
            {
                new PolecatPersistenceFrameProvider().ApplyTransactionSupport(chain, container);
            }

            foreach (var collection in candidates)
            {
                collection.UseReturnAction(v => new ForEachPolecatOpFrame(v));
            }
        }
    }
}

internal class ForEachPolecatOpFrame : SyncFrame
{
    private readonly Variable _collection;
    private Variable _session;

    public ForEachPolecatOpFrame(Variable collection)
    {
        _collection = collection;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
        yield return _collection;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Apply each Polecat op to the current document session");
        writer.Write($"foreach (var item_of_{_collection.Usage} in {_collection.Usage}) item_of_{_collection.Usage}.{nameof(IPolecatOp.Execute)}({_session.Usage});");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Access to Polecat related side effect return values from message handlers
/// </summary>
public static class PolecatOps
{
    public static StoreDoc<T> Store<T>(T document) where T : notnull
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        return new StoreDoc<T>(document);
    }

    public static StoreManyDocs<T> StoreMany<T>(params T[] documents) where T : notnull
    {
        if (documents == null) throw new ArgumentNullException(nameof(documents));
        return new StoreManyDocs<T>(documents);
    }

    public static InsertDoc<T> Insert<T>(T document) where T : notnull
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        return new InsertDoc<T>(document);
    }

    public static UpdateDoc<T> Update<T>(T document) where T : notnull
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        return new UpdateDoc<T>(document);
    }

    public static DeleteDoc<T> Delete<T>(T document) where T : notnull
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        return new DeleteDoc<T>(document);
    }

    public static DeleteDocById<T> Delete<T>(string id) where T : class
    {
        if (id == null) throw new ArgumentNullException(nameof(id));
        return new DeleteDocById<T>(id);
    }

    public static DeleteDocById<T> Delete<T>(Guid id) where T : class
    {
        return new DeleteDocById<T>(id);
    }

    public static DeleteDocById<T> Delete<T>(int id) where T : class
    {
        return new DeleteDocById<T>(id);
    }

    public static DeleteDocById<T> Delete<T>(long id) where T : class
    {
        return new DeleteDocById<T>(id);
    }

    public static DeleteDocById<T> Delete<T>(object id) where T : class
    {
        if (id == null) throw new ArgumentNullException(nameof(id));
        return new DeleteDocById<T>(id);
    }

    public static DeleteDocWhere<T> DeleteWhere<T>(Expression<Func<T, bool>> expression) where T : class
    {
        if (expression == null) throw new ArgumentNullException(nameof(expression));
        return new DeleteDocWhere<T>(expression);
    }

    public static StartStream<T> StartStream<T>(Guid streamId, params object[] events) where T : class
    {
        return new StartStream<T>(streamId, events);
    }

    public static IStartStream StartStream<T>(params object[] events) where T : class
    {
        var streamId = CombGuidIdGeneration.NewGuid();
        return new StartStream<T>(streamId, events);
    }

    public static IStartStream StartStream<T>(string streamKey, params object[] events) where T : class
    {
        return new StartStream<T>(streamKey, events);
    }

    public static NoOp Nothing() => new NoOp();
}

public class NoOp : IPolecatOp
{
    public void Execute(IDocumentSession session)
    {
        // nothing
    }
}

public interface IStartStream : IPolecatOp
{
    string StreamKey { get; }
    Guid StreamId { get; }
    Type AggregateType { get; }
    IReadOnlyList<object> Events { get; }
}

public class StartStream<T> : IStartStream where T : class
{
    public string StreamKey { get; } = string.Empty;
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

    public StartStream(Guid streamId, IList<object> events)
    {
        StreamId = streamId;
        Events.AddRange(events);
    }

    public StartStream(string streamKey, IList<object> events)
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
            if (StreamKey.IsNotEmpty())
            {
                session.Events.StartStream<T>(StreamKey, Events.ToArray());
            }
            else
            {
                session.Events.StartStream<T>(Events.ToArray());
            }
        }
        else
        {
            session.Events.StartStream<T>(StreamId, Events.ToArray());
        }
    }

    Type IStartStream.AggregateType => typeof(T);
    IReadOnlyList<object> IStartStream.Events => Events;
}

public class StoreDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;
    public StoreDoc(T document) : base(document) { _document = document; }
    public override void Execute(IDocumentSession session) { session.Store(_document); }
}

public class StoreManyDocs<T> : DocumentsOp where T : notnull
{
    private readonly T[] _documents;
    public StoreManyDocs(params T[] documents) : base(documents.Cast<object>().ToArray()) { _documents = documents; }
    public StoreManyDocs(IList<T> documents) : this(documents.ToArray()) { }
    public override void Execute(IDocumentSession session) { session.Store(_documents); }
}

public class InsertDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;
    public InsertDoc(T document) : base(document) { _document = document; }
    public override void Execute(IDocumentSession session) { session.Insert(_document); }
}

public class UpdateDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;
    public UpdateDoc(T document) : base(document) { _document = document; }
    public override void Execute(IDocumentSession session) { session.Update(_document); }
}

public class DeleteDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;
    public DeleteDoc(T document) : base(document) { _document = document; }
    public override void Execute(IDocumentSession session) { session.Delete(_document); }
}

public class DeleteDocById<T> : IPolecatOp where T : class
{
    private readonly object _id;
    public DeleteDocById(object id) { _id = id; }

    public void Execute(IDocumentSession session)
    {
        switch (_id)
        {
            case string idAsString: session.Delete<T>(idAsString); break;
            case Guid idAsGuid: session.Delete<T>(idAsGuid); break;
            case long idAsLong: session.Delete<T>(idAsLong); break;
            case int idAsInt: session.Delete<T>(idAsInt); break;
            default: throw new InvalidOperationException($"Cannot delete by id of type {_id.GetType()}"); break;
        }
    }
}

public class DeleteDocWhere<T> : IPolecatOp where T : class
{
    private readonly Expression<Func<T, bool>> _expression;
    public DeleteDocWhere(Expression<Func<T, bool>> expression) { _expression = expression; }
    public void Execute(IDocumentSession session) { session.DeleteWhere(_expression); }
}

public abstract class DocumentOp : IPolecatOp
{
    public object Document { get; }
    protected DocumentOp(object document) { Document = document; }
    public abstract void Execute(IDocumentSession session);
}

public abstract class DocumentsOp : IPolecatOp
{
    public object[] Documents { get; }
    protected DocumentsOp(params object[] documents) { Documents = documents; }
    public abstract void Execute(IDocumentSession session);
}
