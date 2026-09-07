using System.Linq.Expressions;
using Polecat;

namespace Wolverine.Polecat;

/// <summary>
/// Document side effects that round out the parity with Polecat's own
/// <see cref="IDocumentOperations" />: hard deletes, soft delete reversal, version- and
/// revision-checked updates, and raw SQL.
/// </summary>
/// <remarks>
/// The sibling of <c>MartenOps.Documents</c> (GH-4384), checked against Polecat's own session API
/// rather than copied across. Polecat has no <c>InsertObjects</c> / <c>DeleteObjects</c>, no
/// <c>TryUpdateRevision</c>, no patching API, and no placeholder overload of
/// <c>QueueSqlCommand</c>, so none of those have an op here — an op whose <c>Execute</c> could only
/// throw is worse than the absence of one.
/// </remarks>
public static partial class PolecatOps
{
    /// <summary>
    /// Return a side effect of "hard" deleting the specified document in Polecat, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    public static HardDeleteDoc<T> HardDelete<T>(T document) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new HardDeleteDoc<T>(document);
    }

    /// <summary>
    /// Return a side effect of "hard" deleting a document by id in Polecat, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    public static HardDeleteDocById<T> HardDelete<T>(string id) where T : class => new(id);

    /// <summary>
    /// Return a side effect of "hard" deleting a document by id in Polecat, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    public static HardDeleteDocById<T> HardDelete<T>(Guid id) where T : class => new(id);

    /// <summary>
    /// Return a side effect of "hard" deleting a document by id in Polecat, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    public static HardDeleteDocById<T> HardDelete<T>(int id) where T : class => new(id);

    /// <summary>
    /// Return a side effect of "hard" deleting a document by id in Polecat, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    public static HardDeleteDocById<T> HardDelete<T>(long id) where T : class => new(id);

    /// <summary>
    /// Return a side effect of "hard" deleting every document matching the provided filter,
    /// deleting the underlying database rows even for a soft-deleted document type
    /// </summary>
    public static HardDeleteDocWhere<T> HardDeleteWhere<T>(Expression<Func<T, bool>> expression) where T : notnull
        => new(expression);

    /// <summary>
    /// Return a side effect of reversing the soft deletion of every document of a
    /// soft-deleted document type matching the provided filter
    /// </summary>
    public static UndoDeleteDocWhere<T> UndoDeleteWhere<T>(Expression<Func<T, bool>> expression) where T : notnull
        => new(expression);

    /// <summary>
    /// Return a side effect of updating the specified document in Polecat while asserting that
    /// the currently stored version matches the supplied version. Causes a ConcurrencyException
    /// on SaveChangesAsync() if the stored version has moved on
    /// </summary>
    public static UpdateDocExpectedVersion<T> UpdateExpectedVersion<T>(T document, Guid version) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new UpdateDocExpectedVersion<T>(document, version);
    }

    /// <summary>
    /// Return a side effect of updating the specified document in Polecat with a new revision.
    /// Causes a ConcurrencyException on SaveChangesAsync() if the stored revision is greater
    /// than or equal to the supplied revision
    /// </summary>
    public static UpdateDocRevision<T> UpdateRevision<T>(T document, long revision) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new UpdateDocRevision<T>(document, revision);
    }

    /// <summary>
    /// Return a side effect of queueing a raw SQL command to run inside the same transaction as
    /// the rest of the session's work. "?" denotes a positional parameter
    /// </summary>
    public static QueueSqlCommandOp QueueSqlCommand(string sql, params object[] parameterValues)
        => new(sql, parameterValues);
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

public class HardDeleteDocById<T> : ITenantedPolecatOp where T : class
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

        // Unlike Delete<T>(), Polecat's HardDelete<T>() has no object-typed overload to fall back
        // on, so an id type it cannot dispatch would only blow up inside Execute() -- long after
        // the handler returned a side effect that looked perfectly valid. Reject it at the point
        // of construction, where the caller can still see it in a unit test.
        if (id is not (string or Guid or long or int))
        {
            throw new ArgumentOutOfRangeException(nameof(id),
                $"Polecat can only hard delete by string, Guid, long, or int id, but got {id.GetType().FullName}");
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

public class HardDeleteDocWhere<T> : ITenantedPolecatOp where T : notnull
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

public class UndoDeleteDocWhere<T> : ITenantedPolecatOp where T : notnull
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

public class QueueSqlCommandOp : ITenantedPolecatOp
{
    public QueueSqlCommandOp(string sql, params object[] parameterValues)
    {
        Sql = sql ?? throw new ArgumentNullException(nameof(sql));
        ParameterValues = parameterValues ?? throw new ArgumentNullException(nameof(parameterValues));
    }

    public string Sql { get; }

    public object[] ParameterValues { get; }

    /// <summary>
    /// Optional tenant id. When set, the operation will be scoped to the specified tenant.
    /// Note that this only routes the command to that tenant's database in a
    /// database-per-tenant setup; it does not add any tenant filtering to the SQL itself
    /// </summary>
    public string? TenantId { get; set; }

    public void Execute(IDocumentSession session)
    {
        IDocumentOperations target = TenantId != null ? session.ForTenant(TenantId) : session;
        target.QueueSqlCommand(Sql, ParameterValues);
    }
}
