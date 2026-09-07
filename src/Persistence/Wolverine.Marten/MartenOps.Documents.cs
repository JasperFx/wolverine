using System.Linq.Expressions;
using JasperFx.Events;
using Marten;
using Marten.Patching;

namespace Wolverine.Marten;

/// <summary>
/// Document side effects that round out the parity with Marten's own
/// <see cref="IDocumentOperations" />: hard deletes, soft delete reversal, mixed
/// insert/delete batches, revision-checked updates, patching, and raw SQL.
/// </summary>
public static partial class MartenOps
{
    /// <summary>
    /// Return a side effect of "hard" deleting the specified document in Marten, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    /// <param name="document"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static HardDeleteDoc<T> HardDelete<T>(T document) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new HardDeleteDoc<T>(document);
    }

    /// <summary>
    /// Return a side effect of "hard" deleting a document by id in Marten, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    /// <param name="id"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static HardDeleteDocById<T> HardDelete<T>(string id) where T : notnull
    {
        return new HardDeleteDocById<T>(id);
    }

    /// <summary>
    /// Return a side effect of "hard" deleting a document by id in Marten, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    /// <param name="id"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static HardDeleteDocById<T> HardDelete<T>(Guid id) where T : notnull
    {
        return new HardDeleteDocById<T>(id);
    }

    /// <summary>
    /// Return a side effect of "hard" deleting a document by id in Marten, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    /// <param name="id"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static HardDeleteDocById<T> HardDelete<T>(int id) where T : notnull
    {
        return new HardDeleteDocById<T>(id);
    }

    /// <summary>
    /// Return a side effect of "hard" deleting a document by id in Marten, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    /// <param name="id"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static HardDeleteDocById<T> HardDelete<T>(long id) where T : notnull
    {
        return new HardDeleteDocById<T>(id);
    }

    /// <summary>
    /// Return a side effect of "hard" deleting every document matching the provided filter,
    /// deleting the underlying database rows even for a soft-deleted document type
    /// </summary>
    /// <param name="expression"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static HardDeleteDocWhere<T> HardDeleteWhere<T>(Expression<Func<T, bool>> expression) where T : notnull
    {
        return new HardDeleteDocWhere<T>(expression);
    }

    /// <summary>
    /// Return a side effect of reversing the soft deletion of every document of a
    /// soft-deleted document type matching the provided filter
    /// </summary>
    /// <param name="expression"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static UndoDeleteDocWhere<T> UndoDeleteWhere<T>(Expression<Func<T, bool>> expression) where T : notnull
    {
        return new UndoDeleteDocWhere<T>(expression);
    }

    /// <summary>
    /// Return a side effect of inserting an enumerable of potentially mixed document types in
    /// Marten. Unlike <see cref="StoreObjects(object[])" /> this will fail on the next
    /// SaveChangesAsync() if any of the documents already exist
    /// </summary>
    /// <param name="documents"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static InsertObjects InsertObjects(params object[] documents)
    {
        if (documents == null)
        {
            throw new ArgumentNullException(nameof(documents));
        }

        return new InsertObjects(documents);
    }

    /// <summary>
    /// Return a side effect of deleting an enumerable of potentially mixed document types in Marten
    /// </summary>
    /// <param name="documents"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static DeleteObjects DeleteObjects(params object[] documents)
    {
        if (documents == null)
        {
            throw new ArgumentNullException(nameof(documents));
        }

        return new DeleteObjects(documents);
    }

    /// <summary>
    /// Return a side effect of updating the specified document in Marten while asserting that
    /// the currently stored version matches the supplied version. Causes a ConcurrencyException
    /// on SaveChangesAsync() if the stored version has moved on
    /// </summary>
    /// <param name="document"></param>
    /// <param name="version"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static UpdateDocExpectedVersion<T> UpdateExpectedVersion<T>(T document, Guid version) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new UpdateDocExpectedVersion<T>(document, version);
    }

    /// <summary>
    /// Return a side effect of updating the specified document in Marten with a new revision.
    /// Causes a ConcurrencyException on SaveChangesAsync() if the stored revision is greater
    /// than or equal to the supplied revision
    /// </summary>
    /// <param name="document"></param>
    /// <param name="revision"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static UpdateDocRevision<T> UpdateRevision<T>(T document, long revision) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new UpdateDocRevision<T>(document, revision);
    }

    /// <summary>
    /// Return a side effect of updating the specified document in Marten with a new revision,
    /// silently doing nothing if the stored revision is greater than or equal to the supplied
    /// revision
    /// </summary>
    /// <param name="document"></param>
    /// <param name="revision"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static TryUpdateDocRevision<T> TryUpdateRevision<T>(T document, long revision) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new TryUpdateDocRevision<T>(document, revision);
    }

    /// <summary>
    /// Return a side effect of patching the single document of type T with the given id
    /// </summary>
    /// <param name="id"></param>
    /// <param name="configure">Applied to Marten's fluent patch API when the side effect executes</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static PatchDoc<T> Patch<T>(string id, Action<IPatchExpression<T>> configure) where T : notnull
    {
        return new PatchDoc<T>(id, configure);
    }

    /// <summary>
    /// Return a side effect of patching the single document of type T with the given id
    /// </summary>
    /// <param name="id"></param>
    /// <param name="configure">Applied to Marten's fluent patch API when the side effect executes</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static PatchDoc<T> Patch<T>(Guid id, Action<IPatchExpression<T>> configure) where T : notnull
    {
        return new PatchDoc<T>(id, configure);
    }

    /// <summary>
    /// Return a side effect of patching the single document of type T with the given id
    /// </summary>
    /// <param name="id"></param>
    /// <param name="configure">Applied to Marten's fluent patch API when the side effect executes</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static PatchDoc<T> Patch<T>(int id, Action<IPatchExpression<T>> configure) where T : notnull
    {
        return new PatchDoc<T>(id, configure);
    }

    /// <summary>
    /// Return a side effect of patching the single document of type T with the given id
    /// </summary>
    /// <param name="id"></param>
    /// <param name="configure">Applied to Marten's fluent patch API when the side effect executes</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static PatchDoc<T> Patch<T>(long id, Action<IPatchExpression<T>> configure) where T : notnull
    {
        return new PatchDoc<T>(id, configure);
    }

    /// <summary>
    /// Return a side effect of patching every document of type T matching the provided filter
    /// </summary>
    /// <param name="filter"></param>
    /// <param name="configure">Applied to Marten's fluent patch API when the side effect executes</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static PatchDoc<T> PatchWhere<T>(Expression<Func<T, bool>> filter, Action<IPatchExpression<T>> configure)
        where T : notnull
    {
        return new PatchDoc<T>(filter, configure);
    }

    /// <summary>
    /// Return a side effect of enlisting a raw SQL command into the same batched unit of work
    /// as the rest of the Wolverine handler's Marten operations. Use "?" placeholders to denote
    /// parameter values
    /// </summary>
    /// <param name="sql"></param>
    /// <param name="parameterValues"></param>
    /// <returns></returns>
    public static QueueSqlCommandOp QueueSqlCommand(string sql, params object[] parameterValues)
    {
        return new QueueSqlCommandOp(sql, parameterValues);
    }

    /// <summary>
    /// Return a side effect of enlisting a raw SQL command into the same batched unit of work
    /// as the rest of the Wolverine handler's Marten operations, using a custom placeholder
    /// character for the positional parameters
    /// </summary>
    /// <param name="placeholder"></param>
    /// <param name="sql"></param>
    /// <param name="parameterValues"></param>
    /// <returns></returns>
    public static QueueSqlCommandOp QueueSqlCommand(char placeholder, string sql, params object[] parameterValues)
    {
        return new QueueSqlCommandOp(sql, parameterValues) { Placeholder = placeholder };
    }
}

public class HardDeleteDoc<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public HardDeleteDoc(T document) : base(document)
    {
        _document = document;
    }

    public HardDeleteDoc(T document, string tenantId) : base(document, tenantId)
    {
        _document = document;
    }

    public override void Execute(IDocumentSession session)
    {
        ResolveSession(session).HardDelete(_document);
    }
}

public class HardDeleteDocById<T> : ITenantedMartenOp where T : notnull
{
    private readonly object _id;

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant
    /// </summary>
    public string? TenantId { get; set; }

    public HardDeleteDocById(object id)
    {
        if (id == null)
        {
            throw new ArgumentNullException(nameof(id));
        }

        // Unlike Delete<T>(), Marten's HardDelete<T>() has no object-typed overload to fall back
        // on, so an id type it cannot dispatch would only blow up inside Execute() - long after
        // the handler returned a side effect that looked perfectly valid. Reject it at the point
        // of construction, where the caller can still see it in a unit test.
        if (id is not (string or Guid or long or int))
        {
            throw new ArgumentOutOfRangeException(nameof(id),
                $"Marten can only hard delete by string, Guid, long, or int id, but got {id.GetType().FullName}");
        }

        _id = id;
    }

    public HardDeleteDocById(object id, string tenantId) : this(id)
    {
        TenantId = tenantId;
    }

    public void Execute(IDocumentSession session)
    {
        IDocumentOperations target = TenantId != null ? session.ForTenant(TenantId) : session;
        switch (_id)
        {
            case string idAsString:
                target.HardDelete<T>(idAsString);
                break;
            case Guid idAsGuid:
                target.HardDelete<T>(idAsGuid);
                break;
            case long idAsLong:
                target.HardDelete<T>(idAsLong);
                break;
            case int idAsInt:
                target.HardDelete<T>(idAsInt);
                break;
        }
    }
}

public class HardDeleteDocWhere<T> : ITenantedMartenOp where T : notnull
{
    private readonly Expression<Func<T, bool>> _expression;

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant
    /// </summary>
    public string? TenantId { get; set; }

    public HardDeleteDocWhere(Expression<Func<T, bool>> expression)
    {
        _expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    public HardDeleteDocWhere(Expression<Func<T, bool>> expression, string tenantId) : this(expression)
    {
        TenantId = tenantId;
    }

    public void Execute(IDocumentSession session)
    {
        IDocumentOperations target = TenantId != null ? session.ForTenant(TenantId) : session;
        target.HardDeleteWhere(_expression);
    }
}

public class UndoDeleteDocWhere<T> : ITenantedMartenOp where T : notnull
{
    private readonly Expression<Func<T, bool>> _expression;

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant
    /// </summary>
    public string? TenantId { get; set; }

    public UndoDeleteDocWhere(Expression<Func<T, bool>> expression)
    {
        _expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    public UndoDeleteDocWhere(Expression<Func<T, bool>> expression, string tenantId) : this(expression)
    {
        TenantId = tenantId;
    }

    public void Execute(IDocumentSession session)
    {
        IDocumentOperations target = TenantId != null ? session.ForTenant(TenantId) : session;
        target.UndoDeleteWhere(_expression);
    }
}

public class InsertObjects : DocumentsOp
{
    public InsertObjects(params object[] documents) : base(documents) { }

    public InsertObjects(IList<object> documents) : this(documents.ToArray()) { }

    public InsertObjects(string tenantId, params object[] documents) : base(tenantId, documents) { }

    public InsertObjects With(object[] documents)
    {
        Documents.AddRange(documents);
        return this;
    }

    public InsertObjects With(object document)
    {
        Documents.Add(document);
        return this;
    }

    public override void Execute(IDocumentSession session)
    {
        ResolveSession(session).InsertObjects(Documents);
    }
}

public class DeleteObjects : DocumentsOp
{
    public DeleteObjects(params object[] documents) : base(documents) { }

    public DeleteObjects(IList<object> documents) : this(documents.ToArray()) { }

    public DeleteObjects(string tenantId, params object[] documents) : base(tenantId, documents) { }

    public DeleteObjects With(object[] documents)
    {
        Documents.AddRange(documents);
        return this;
    }

    public DeleteObjects With(object document)
    {
        Documents.Add(document);
        return this;
    }

    public override void Execute(IDocumentSession session)
    {
        ResolveSession(session).DeleteObjects(Documents);
    }
}

public class UpdateDocExpectedVersion<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public UpdateDocExpectedVersion(T document, Guid version) : base(document)
    {
        _document = document;
        Version = version;
    }

    public Guid Version { get; }

    public override void Execute(IDocumentSession session)
    {
        ResolveSession(session).UpdateExpectedVersion(_document, Version);
    }
}

public class UpdateDocRevision<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public UpdateDocRevision(T document, long revision) : base(document)
    {
        _document = document;
        Revision = revision;
    }

    public long Revision { get; }

    public override void Execute(IDocumentSession session)
    {
        ResolveSession(session).UpdateRevision(_document, Revision);
    }
}

public class TryUpdateDocRevision<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public TryUpdateDocRevision(T document, long revision) : base(document)
    {
        _document = document;
        Revision = revision;
    }

    public long Revision { get; }

    public override void Execute(IDocumentSession session)
    {
        ResolveSession(session).TryUpdateRevision(_document, Revision);
    }
}

public class PatchDoc<T> : ITenantedMartenOp where T : notnull
{
    private readonly Action<IPatchExpression<T>> _configure;
    private readonly Expression<Func<T, bool>>? _filter;
    private readonly object? _id;

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant
    /// </summary>
    public string? TenantId { get; set; }

    public PatchDoc(object id, Action<IPatchExpression<T>> configure)
    {
        if (id == null)
        {
            throw new ArgumentNullException(nameof(id));
        }

        // Same story as HardDeleteDocById: Marten's Patch<T>() overloads cover string, Guid,
        // long, and int only, so fail fast rather than at SaveChangesAsync() time.
        if (id is not (string or Guid or long or int))
        {
            throw new ArgumentOutOfRangeException(nameof(id),
                $"Marten can only patch by string, Guid, long, or int id, but got {id.GetType().FullName}");
        }

        _id = id;
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }

    public PatchDoc(Expression<Func<T, bool>> filter, Action<IPatchExpression<T>> configure)
    {
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }

    public void Execute(IDocumentSession session)
    {
        IDocumentOperations target = TenantId != null ? session.ForTenant(TenantId) : session;

        var expression = _filter != null
            ? target.Patch(_filter)
            : _id switch
            {
                string idAsString => target.Patch<T>(idAsString),
                Guid idAsGuid => target.Patch<T>(idAsGuid),
                long idAsLong => target.Patch<T>(idAsLong),
                int idAsInt => target.Patch<T>(idAsInt),
                _ => throw new ArgumentOutOfRangeException(nameof(_id))
            };

        _configure(expression);
    }
}

public class QueueSqlCommandOp : ITenantedMartenOp
{
    public QueueSqlCommandOp(string sql, params object[] parameterValues)
    {
        Sql = sql ?? throw new ArgumentNullException(nameof(sql));
        ParameterValues = parameterValues ?? throw new ArgumentNullException(nameof(parameterValues));
    }

    public string Sql { get; }

    public object[] ParameterValues { get; }

    /// <summary>
    /// Optional override of the "?" character that denotes a positional parameter in the SQL
    /// </summary>
    public char? Placeholder { get; set; }

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant.
    /// Note that this only routes the command to that tenant's database in a
    /// database-per-tenant setup; it does not add any tenant filtering to the SQL itself
    /// </summary>
    public string? TenantId { get; set; }

    public void Execute(IDocumentSession session)
    {
        IDocumentOperations target = TenantId != null ? session.ForTenant(TenantId) : session;

        if (Placeholder.HasValue)
        {
            target.QueueSqlCommand(Placeholder.Value, Sql, ParameterValues);
        }
        else
        {
            target.QueueSqlCommand(Sql, ParameterValues);
        }
    }
}
