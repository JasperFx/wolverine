using System.Linq.Expressions;
using Fisher;
using Fisher.Patching;

namespace Wolverine.Fisher;

/// <summary>
/// Document side effects that round out the parity with Fisher's own
/// <see cref="IDocumentOperations" />: hard deletes, soft delete reversal, revision-checked
/// updates, patching, and raw SQL.
/// </summary>
/// <remarks>
/// The sibling of <c>MartenOps.Documents</c> (GH-4384), checked against Fisher's own session API
/// rather than copied across. Fisher has no <c>InsertObjects</c> / <c>DeleteObjects</c> and no
/// <c>UpdateExpectedVersion</c>, so none of those have an op here — an op whose <c>Execute</c>
/// could only throw is worse than the absence of one. Its revision API is <c>int</c>-based rather
/// than <c>long</c>, and that is what these signatures take: widening it here would let a caller
/// pass a value the store cannot hold.
/// </remarks>
public static partial class FisherOps
{
    /// <summary>
    /// Return a side effect of "hard" deleting the specified document in Fisher, deleting the
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
    /// Return a side effect of "hard" deleting a document by id in Fisher, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    public static HardDeleteDocById<T> HardDelete<T>(string id) where T : class => new(id);

    /// <summary>
    /// Return a side effect of "hard" deleting a document by id in Fisher, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    public static HardDeleteDocById<T> HardDelete<T>(Guid id) where T : class => new(id);

    /// <summary>
    /// Return a side effect of "hard" deleting a document by id in Fisher, deleting the
    /// underlying database row even for a soft-deleted document type
    /// </summary>
    public static HardDeleteDocById<T> HardDelete<T>(int id) where T : class => new(id);

    /// <summary>
    /// Return a side effect of "hard" deleting a document by id in Fisher, deleting the
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
    /// Return a side effect of updating the specified document in Fisher with a new revision.
    /// Causes a ConcurrencyException on SaveChangesAsync() if the stored revision is greater
    /// than or equal to the supplied revision
    /// </summary>
    public static UpdateDocRevision<T> UpdateRevision<T>(T document, int revision) where T : notnull
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        return new UpdateDocRevision<T>(document, revision);
    }

    /// <summary>
    /// Return a side effect of updating the specified document in Fisher with a new revision,
    /// silently doing nothing if the stored revision is greater than or equal to the supplied
    /// revision
    /// </summary>
    public static TryUpdateDocRevision<T> TryUpdateRevision<T>(T document, int revision) where T : notnull
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
    public static PatchDoc<T> Patch<T>(object id, Action<IPatchExpression<T>> configure) where T : notnull
        => new(id, configure);

    /// <summary>
    /// Return a side effect of patching every document of type T matching the supplied filter
    /// </summary>
    public static PatchDoc<T> PatchWhere<T>(Expression<Func<T, bool>> filter, Action<IPatchExpression<T>> configure)
        where T : notnull
        => new(filter, configure);

    /// <summary>
    /// Return a side effect of queueing a raw SQL command to run inside the same transaction as
    /// the rest of the session's work. "?" denotes a positional parameter
    /// </summary>
    public static QueueSqlCommandOp QueueSqlCommand(string sql, params object[] parameterValues)
        => new(sql, parameterValues);

    /// <summary>
    /// Return a side effect of queueing a raw SQL command, overriding the character that denotes
    /// a positional parameter — for SQL that needs a literal "?" of its own
    /// </summary>
    public static QueueSqlCommandOp QueueSqlCommand(char placeholder, string sql, params object[] parameterValues)
        => new(sql, parameterValues) { Placeholder = placeholder };
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

public class HardDeleteDocById<T> : ITenantedFisherOp where T : class
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

        // Unlike Delete<T>(), Fisher's HardDelete<T>() has no object-typed overload to fall back
        // on, so an id type it cannot dispatch would only blow up inside Execute() -- long after
        // the handler returned a side effect that looked perfectly valid. Reject it at the point
        // of construction, where the caller can still see it in a unit test.
        if (id is not (string or Guid or long or int))
        {
            throw new ArgumentOutOfRangeException(nameof(id),
                $"Fisher can only hard delete by string, Guid, long, or int id, but got {id.GetType().FullName}");
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

public class HardDeleteDocWhere<T> : ITenantedFisherOp where T : notnull
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

public class UndoDeleteDocWhere<T> : ITenantedFisherOp where T : notnull
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

public class UpdateDocRevision<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public UpdateDocRevision(T document, int revision) : base(document)
    {
        _document = document;
        Revision = revision;
    }

    public int Revision { get; }

    public override void Execute(IDocumentSession session)
    {
        ResolveSession(session).UpdateRevision(_document, Revision);
    }
}

public class TryUpdateDocRevision<T> : DocumentOp where T : notnull
{
    private readonly T _document;

    public TryUpdateDocRevision(T document, int revision) : base(document)
    {
        _document = document;
        Revision = revision;
    }

    public int Revision { get; }

    public override void Execute(IDocumentSession session)
    {
        ResolveSession(session).TryUpdateRevision(_document, Revision);
    }
}

public class PatchDoc<T> : ITenantedFisherOp where T : notnull
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

        // Same story as HardDeleteDocById: Fisher's Patch<T>() overloads cover string, Guid,
        // long, and int only, so fail fast rather than at SaveChangesAsync() time.
        if (id is not (string or Guid or long or int))
        {
            throw new ArgumentOutOfRangeException(nameof(id),
                $"Fisher can only patch by string, Guid, long, or int id, but got {id.GetType().FullName}");
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

public class QueueSqlCommandOp : ITenantedFisherOp
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
