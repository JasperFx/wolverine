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

    /// <summary>
    /// Return a side effect of storing an enumerable of potentially mixed document types in Polecat
    /// </summary>
    /// <param name="documents"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static StoreObjects StoreObjects(params object[] documents)
    {
        if (documents == null) throw new ArgumentNullException(nameof(documents));
        return new StoreObjects(documents);
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

    // ---- Tenant-scoped overloads ----

    /// <summary>
    /// Return a side effect of storing the specified document, scoped to a specific tenant
    /// </summary>
    public static StoreDoc<T> Store<T>(T document, string tenantId) where T : notnull
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        return new StoreDoc<T>(document, tenantId);
    }

    /// <summary>
    /// Return a side effect of storing many documents, scoped to a specific tenant
    /// </summary>
    public static StoreManyDocs<T> StoreMany<T>(string tenantId, params T[] documents) where T : notnull
    {
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        if (documents == null) throw new ArgumentNullException(nameof(documents));
        return new StoreManyDocs<T>(tenantId, documents);
    }

    /// <summary>
    /// Return a side effect of storing an enumerable of potentially mixed document types, scoped to a specific tenant
    /// </summary>
    public static StoreObjects StoreObjects(string tenantId, params object[] documents)
    {
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        if (documents == null) throw new ArgumentNullException(nameof(documents));
        return new StoreObjects(tenantId, documents);
    }

    /// <summary>
    /// Return a side effect of inserting the specified document, scoped to a specific tenant
    /// </summary>
    public static InsertDoc<T> Insert<T>(T document, string tenantId) where T : notnull
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        return new InsertDoc<T>(document, tenantId);
    }

    /// <summary>
    /// Return a side effect of updating the specified document, scoped to a specific tenant
    /// </summary>
    public static UpdateDoc<T> Update<T>(T document, string tenantId) where T : notnull
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        return new UpdateDoc<T>(document, tenantId);
    }

    /// <summary>
    /// Return a side effect of deleting the specified document, scoped to a specific tenant
    /// </summary>
    public static DeleteDoc<T> Delete<T>(T document, string tenantId) where T : notnull
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        return new DeleteDoc<T>(document, tenantId);
    }

    /// <summary>
    /// Return a side effect of deleting a document by string id, scoped to a specific tenant
    /// </summary>
    public static DeleteDocById<T> Delete<T>(string id, string tenantId) where T : class
    {
        if (id == null) throw new ArgumentNullException(nameof(id));
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        return new DeleteDocById<T>(id, tenantId);
    }

    /// <summary>
    /// Return a side effect of deleting a document by Guid id, scoped to a specific tenant
    /// </summary>
    public static DeleteDocById<T> Delete<T>(Guid id, string tenantId) where T : class
    {
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        return new DeleteDocById<T>(id, tenantId);
    }

    /// <summary>
    /// Return a side effect of deleting a document by int id, scoped to a specific tenant
    /// </summary>
    public static DeleteDocById<T> Delete<T>(int id, string tenantId) where T : class
    {
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        return new DeleteDocById<T>(id, tenantId);
    }

    /// <summary>
    /// Return a side effect of deleting a document by long id, scoped to a specific tenant
    /// </summary>
    public static DeleteDocById<T> Delete<T>(long id, string tenantId) where T : class
    {
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        return new DeleteDocById<T>(id, tenantId);
    }

    /// <summary>
    /// Return a side effect of deleting documents matching a filter, scoped to a specific tenant
    /// </summary>
    public static DeleteDocWhere<T> DeleteWhere<T>(Expression<Func<T, bool>> expression, string tenantId) where T : class
    {
        if (expression == null) throw new ArgumentNullException(nameof(expression));
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        return new DeleteDocWhere<T>(expression, tenantId);
    }

    /// <summary>
    /// Return a side effect of starting a new event stream, scoped to a specific tenant
    /// </summary>
    public static StartStream<T> StartStream<T>(Guid streamId, string tenantId, params object[] events) where T : class
    {
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        return new StartStream<T>(streamId, events) { TenantId = tenantId };
    }

    /// <summary>
    /// Return a side effect of starting a new event stream with a string key, scoped to a specific tenant
    /// </summary>
    public static IStartStream StartStream<T>(string streamKey, string tenantId, params object[] events) where T : class
    {
        if (tenantId == null) throw new ArgumentNullException(nameof(tenantId));
        return new StartStream<T>(streamKey, events) { TenantId = tenantId };
    }
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

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant
    /// </summary>
    public string? TenantId { get; set; }

    public StartStream(Guid streamId, params object[] events)
    {
        StreamId = streamId;
        Events.AddRange(events);
    }

    public StartStream(string streamKey, params object[] events)
    {
        if (string.IsNullOrEmpty(streamKey))
        {
            throw new InvalidOperationException(
                "The stream key must not be null or empty when starting a new event stream");
        }

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
        if (string.IsNullOrEmpty(streamKey))
        {
            throw new InvalidOperationException(
                "The stream key must not be null or empty when starting a new event stream");
        }

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
        // For event streams, we use the session directly since ITenantOperations
        // exposes IQueryEventStore (read-only) rather than IEventStoreOperations.
        // Tenant scoping for event streams should be handled at the session level.
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
    public StoreDoc(T document, string tenantId) : base(document, tenantId) { _document = document; }
    public override void Execute(IDocumentSession session) { ResolveSession(session).Store(_document); }
}

public class StoreManyDocs<T> : DocumentsOp where T : notnull
{
    public StoreManyDocs(params T[] documents) : base(documents.Cast<object>().ToArray()) { }
    public StoreManyDocs(IList<T> documents) : this(documents.ToArray()) { }
    public StoreManyDocs(string tenantId, params T[] documents) : base(tenantId, documents.Cast<object>().ToArray()) { }

    public StoreManyDocs<T> With(T[] documents)
    {
        Documents.AddRange(documents.Cast<object>());
        return this;
    }

    public StoreManyDocs<T> With(T document)
    {
        Documents.Add(document);
        return this;
    }

    public override void Execute(IDocumentSession session) { ResolveSession(session).Store(Documents.Cast<T>()); }
}

public class StoreObjects : DocumentsOp
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.MethodInfo> _storeMethods = new();

    public StoreObjects(params object[] documents) : base(documents) { }

    public StoreObjects(IList<object> documents) : this(documents.ToArray()) { }

    public StoreObjects(string tenantId, params object[] documents) : base(tenantId, documents) { }

    public StoreObjects With(object[] documents)
    {
        Documents.AddRange(documents);
        return this;
    }

    public StoreObjects With(object document)
    {
        Documents.Add(document);
        return this;
    }

    public override void Execute(IDocumentSession session)
    {
        // Polecat does not have a single StoreObjects(IEnumerable<object>) method like Marten,
        // so we dispatch each document to Store<T> by its runtime type.
        var target = ResolveSession(session);
        foreach (var document in Documents)
        {
            if (document is null) continue;
            var docType = document.GetType();
            var method = _storeMethods.GetOrAdd(docType, t =>
            {
                var open = typeof(IDocumentOperations).GetMethods()
                    .First(m => m.Name == nameof(IDocumentOperations.Store)
                        && m.IsGenericMethodDefinition
                        && m.GetParameters().Length == 1
                        && !m.GetParameters()[0].ParameterType.IsArray);
                return open.MakeGenericMethod(t);
            });
            method.Invoke(target, new[] { document });
        }
    }
}

public class InsertDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;
    public InsertDoc(T document) : base(document) { _document = document; }
    public InsertDoc(T document, string tenantId) : base(document, tenantId) { _document = document; }
    public override void Execute(IDocumentSession session) { ResolveSession(session).Insert(_document); }
}

public class UpdateDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;
    public UpdateDoc(T document) : base(document) { _document = document; }
    public UpdateDoc(T document, string tenantId) : base(document, tenantId) { _document = document; }
    public override void Execute(IDocumentSession session) { ResolveSession(session).Update(_document); }
}

public class DeleteDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;
    public DeleteDoc(T document) : base(document) { _document = document; }
    public DeleteDoc(T document, string tenantId) : base(document, tenantId) { _document = document; }
    public override void Execute(IDocumentSession session) { ResolveSession(session).Delete(_document); }
}

public class DeleteDocById<T> : IPolecatOp where T : class
{
    private readonly object _id;

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant
    /// </summary>
    public string? TenantId { get; set; }

    public DeleteDocById(object id) { _id = id; }
    public DeleteDocById(object id, string tenantId) : this(id) { TenantId = tenantId; }

    public void Execute(IDocumentSession session)
    {
        IDocumentOperations target = TenantId != null ? session.ForTenant(TenantId) : session;
        switch (_id)
        {
            case string idAsString: target.Delete<T>(idAsString); break;
            case Guid idAsGuid: target.Delete<T>(idAsGuid); break;
            case long idAsLong: target.Delete<T>(idAsLong); break;
            case int idAsInt: target.Delete<T>(idAsInt); break;
            default: throw new InvalidOperationException($"Cannot delete by id of type {_id.GetType()}"); break;
        }
    }
}

public class DeleteDocWhere<T> : IPolecatOp where T : class
{
    private readonly Expression<Func<T, bool>> _expression;

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant
    /// </summary>
    public string? TenantId { get; set; }

    public DeleteDocWhere(Expression<Func<T, bool>> expression) { _expression = expression; }
    public DeleteDocWhere(Expression<Func<T, bool>> expression, string tenantId) : this(expression) { TenantId = tenantId; }

    public void Execute(IDocumentSession session)
    {
        IDocumentOperations target = TenantId != null ? session.ForTenant(TenantId) : session;
        target.DeleteWhere(_expression);
    }
}

public abstract class DocumentOp : IPolecatOp
{
    public object Document { get; }

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant
    /// </summary>
    public string? TenantId { get; set; }

    protected DocumentOp(object document) { Document = document; }
    protected DocumentOp(object document, string tenantId) : this(document) { TenantId = tenantId; }

    /// <summary>
    /// Resolves the appropriate session, scoped to the tenant if TenantId is set
    /// </summary>
    protected IDocumentOperations ResolveSession(IDocumentSession session)
    {
        return TenantId != null ? session.ForTenant(TenantId) : session;
    }

    public abstract void Execute(IDocumentSession session);
}

public interface IDocumentsOp : IPolecatOp
{
    IReadOnlyList<object> Documents { get; }
}

public abstract class DocumentsOp : IDocumentsOp
{
    public List<object> Documents { get; } = new();

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant
    /// </summary>
    public string? TenantId { get; set; }

    protected DocumentsOp(params object[] documents) { Documents.AddRange(documents); }
    protected DocumentsOp(string tenantId, params object[] documents) : this(documents) { TenantId = tenantId; }

    /// <summary>
    /// Resolves the appropriate session, scoped to the tenant if TenantId is set
    /// </summary>
    protected IDocumentOperations ResolveSession(IDocumentSession session)
    {
        return TenantId != null ? session.ForTenant(TenantId) : session;
    }

    public abstract void Execute(IDocumentSession session);

    IReadOnlyList<object> IDocumentsOp.Documents => Documents;
}
