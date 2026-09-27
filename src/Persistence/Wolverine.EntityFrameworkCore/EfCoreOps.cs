using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.EntityFrameworkCore;
using Wolverine.Configuration;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.EntityFrameworkCore.Internals;

namespace Wolverine.EntityFrameworkCore;

#region sample_efcoreop

/// <summary>
///     Base class for an EF Core side effect returned from a message handler or HTTP endpoint,
///     the EF Core counterpart of <c>IMartenOp</c> / <c>IPolecatOp</c> / <c>ICosmosDbOp</c>. Build
///     them with the <see cref="EfCoreOps" /> factory methods.
/// </summary>
public abstract class EfCoreOp : ISideEffectAware
{
    /// <summary>
    ///     Apply this operation against the <see cref="DbContext" /> that owns the handler's transaction.
    ///     Called by generated code; you do not call this yourself.
    /// </summary>
    public abstract Task ExecuteAsync(DbContext dbContext, CancellationToken cancellationToken);

    static Frame ISideEffectAware.BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
    {
        return EfCoreOpFrames.Build(chain, variable, rules, container);
    }
}

#endregion

/// <summary>
///     Marks an <see cref="EfCoreOp" /> whose statement goes to the database on its own rather than
///     through <c>SaveChangesAsync</c>. A chain that returns one is forced into
///     <see cref="Wolverine.Persistence.TransactionMiddlewareMode.Eager" />, because in Lightweight mode
///     there is no transaction around the statement and it commits whether or not the rest of the
///     handler -- the outbox rows included -- ever does.
/// </summary>
public interface IBypassesSaveChanges;

/// <summary>
///     Put this on a handler method or handler type that calls <c>ExecuteUpdateAsync</c>,
///     <c>ExecuteDeleteAsync</c>, <c>Database.ExecuteSqlAsync</c> or a stored procedure through the
///     <see cref="DbContext" /> itself, so Wolverine opens a transaction around the handler even when
///     the application default is
///     <see cref="Wolverine.Persistence.TransactionMiddlewareMode.Lightweight" />.
/// </summary>
/// <remarks>
///     The <see cref="EfCoreOps" /> operations need no such marker; returning one is the declaration.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class RequiresEagerTransactionAttribute : Attribute;

/// <summary>
///     Factory methods for EF Core side effects returned from handlers and HTTP endpoints.
/// </summary>
public static class EfCoreOps
{
    /// <summary>
    ///     A set-based <c>UPDATE</c> against every row matching <paramref name="predicate" />. Under
    ///     conjoined multi-tenancy the tenant predicate is appended for you, and an attempt to set the
    ///     tenant id is refused.
    /// </summary>
    public static ExecuteUpdateOp<T> ExecuteUpdate<T>(Expression<Func<T, bool>> predicate,
        Func<EfCoreSetters<T>, EfCoreSetters<T>> setters) where T : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(setters);

        var recorded = setters(new EfCoreSetters<T>());
        if (recorded.Setters.Count == 0)
        {
            throw new ArgumentException(
                $"An ExecuteUpdate against {typeof(T).Name} has to set at least one property.", nameof(setters));
        }

        return new ExecuteUpdateOp<T>(predicate, recorded);
    }

    /// <summary>
    ///     A set-based <c>DELETE</c> of every row matching <paramref name="predicate" />. Under conjoined
    ///     multi-tenancy the tenant predicate is appended for you.
    /// </summary>
    public static ExecuteDeleteOp<T> ExecuteDelete<T>(Expression<Func<T, bool>> predicate) where T : class
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return new ExecuteDeleteOp<T>(predicate);
    }

    /// <summary>
    ///     Run raw SQL -- a stored procedure call, a refresh of a materialized view -- through
    ///     <c>DbContext.Database.ExecuteSqlAsync</c>. The interpolated values become parameters.
    /// </summary>
    public static ExecuteSqlOp ExecuteSql(FormattableString sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        return new ExecuteSqlOp(sql);
    }

    /// <summary>
    ///     Run raw SQL through <c>DbContext.Database.ExecuteSqlRawAsync</c>, with positional parameters.
    /// </summary>
    public static ExecuteSqlRawOp ExecuteSqlRaw(string sql, params object[] parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);

        return new ExecuteSqlRawOp(sql, parameters ?? []);
    }

    /// <summary>
    ///     Add many new entities in one <c>AddRange</c>, committed by the single <c>SaveChangesAsync</c>
    ///     the transactional middleware already emits.
    /// </summary>
    public static InsertManyOp<T> InsertMany<T>(IEnumerable<T> entities) where T : class
    {
        ArgumentNullException.ThrowIfNull(entities);

        return new InsertManyOp<T>(entities as IReadOnlyList<T> ?? entities.ToList());
    }

    /// <summary>
    ///     Add many new entities in one <c>AddRange</c>, committed by the single <c>SaveChangesAsync</c>
    ///     the transactional middleware already emits.
    /// </summary>
    public static InsertManyOp<T> InsertMany<T>(params T[] entities) where T : class
    {
        ArgumentNullException.ThrowIfNull(entities);

        return new InsertManyOp<T>(entities);
    }
}

/// <summary>
///     A set-based <c>UPDATE</c>. See <see cref="EfCoreOps.ExecuteUpdate{T}" />.
/// </summary>
public class ExecuteUpdateOp<T> : EfCoreOp, IBypassesSaveChanges where T : class
{
    internal ExecuteUpdateOp(Expression<Func<T, bool>> predicate, EfCoreSetters<T> setters)
    {
        Predicate = predicate;
        Setters = setters;
    }

    internal Expression<Func<T, bool>> Predicate { get; }
    internal EfCoreSetters<T> Setters { get; }

    internal bool IgnoresQueryFilters { get; private set; }

    /// <summary>
    ///     Run the update against rows the DbContext's global query filters would otherwise hide -- a
    ///     soft delete filter, say. Refused for an <c>ITenanted</c> entity under conjoined multi-tenancy,
    ///     where dropping the filter means crossing tenants.
    /// </summary>
    public ExecuteUpdateOp<T> IgnoreQueryFilters()
    {
        IgnoresQueryFilters = true;
        return this;
    }

    /// <summary>
    ///     The number of rows the statement changed, available after the operation has run.
    /// </summary>
    public int RowsAffected { get; private set; }

    public override async Task ExecuteAsync(DbContext dbContext, CancellationToken cancellationToken)
    {
        var query = EfCoreOpTenancy.Scope(dbContext, Predicate, IgnoresQueryFilters);
        EfCoreOpTenancy.AssertSettersDoNotMoveTenant(dbContext, Setters);

        RowsAffected = await query.ExecuteUpdateAsync(Setters.ToEfCoreSetters(), cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
///     A set-based <c>DELETE</c>. See <see cref="EfCoreOps.ExecuteDelete{T}" />.
/// </summary>
public class ExecuteDeleteOp<T> : EfCoreOp, IBypassesSaveChanges where T : class
{
    internal ExecuteDeleteOp(Expression<Func<T, bool>> predicate)
    {
        Predicate = predicate;
    }

    internal Expression<Func<T, bool>> Predicate { get; }

    internal bool IgnoresQueryFilters { get; private set; }

    /// <summary>
    ///     Run the delete against rows the DbContext's global query filters would otherwise hide.
    ///     Refused for an <c>ITenanted</c> entity under conjoined multi-tenancy.
    /// </summary>
    public ExecuteDeleteOp<T> IgnoreQueryFilters()
    {
        IgnoresQueryFilters = true;
        return this;
    }

    /// <summary>
    ///     The number of rows the statement deleted, available after the operation has run.
    /// </summary>
    public int RowsAffected { get; private set; }

    public override async Task ExecuteAsync(DbContext dbContext, CancellationToken cancellationToken)
    {
        var query = EfCoreOpTenancy.Scope(dbContext, Predicate, IgnoresQueryFilters);

        RowsAffected = await query.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
///     Raw SQL through <c>DbContext.Database.ExecuteSqlAsync</c>. See <see cref="EfCoreOps.ExecuteSql" />.
/// </summary>
public class ExecuteSqlOp : EfCoreOp, IBypassesSaveChanges
{
    private readonly FormattableString _sql;

    internal ExecuteSqlOp(FormattableString sql)
    {
        _sql = sql;
    }

    /// <summary>
    ///     The number of rows the statement reported, available after the operation has run.
    /// </summary>
    public int RowsAffected { get; private set; }

    public override async Task ExecuteAsync(DbContext dbContext, CancellationToken cancellationToken)
    {
        EfCoreOpTenancy.AssertContextTenantIsEnabled(dbContext);

        RowsAffected = await dbContext.Database.ExecuteSqlAsync(_sql, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
///     Raw SQL through <c>DbContext.Database.ExecuteSqlRawAsync</c>. See
///     <see cref="EfCoreOps.ExecuteSqlRaw" />.
/// </summary>
public class ExecuteSqlRawOp : EfCoreOp, IBypassesSaveChanges
{
    private readonly object[] _parameters;
    private readonly string _sql;

    internal ExecuteSqlRawOp(string sql, object[] parameters)
    {
        _sql = sql;
        _parameters = parameters;
    }

    /// <summary>
    ///     The number of rows the statement reported, available after the operation has run.
    /// </summary>
    public int RowsAffected { get; private set; }

    public override async Task ExecuteAsync(DbContext dbContext, CancellationToken cancellationToken)
    {
        EfCoreOpTenancy.AssertContextTenantIsEnabled(dbContext);

        RowsAffected = await dbContext.Database.ExecuteSqlRawAsync(_sql, _parameters, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
///     Adds many entities in one <c>AddRange</c>. See <see cref="EfCoreOps.InsertMany{T}(IEnumerable{T})" />.
/// </summary>
/// <remarks>
///     Deliberately not an <see cref="IBypassesSaveChanges" />: this one writes through the same
///     <c>SaveChangesAsync</c> as everything else the handler did, so it does not need -- and does not
///     force -- an explicit transaction of its own.
/// </remarks>
// AOT note (#2746): T is an entity type mapped in a registered DbContext, so the EF Core model
// already roots it -- same justification as EfCoreStorageActionApplier.
[UnconditionalSuppressMessage("Trimming", "IL2091",
    Justification = "T is an entity type mapped in a registered DbContext, which roots it. See AOT guide / #2755.")]
public class InsertManyOp<T> : EfCoreOp where T : class
{
    internal InsertManyOp(IReadOnlyList<T> entities)
    {
        Entities = entities;
    }

    internal IReadOnlyList<T> Entities { get; }

    public override Task ExecuteAsync(DbContext dbContext, CancellationToken cancellationToken)
    {
        return dbContext.Set<T>().AddRangeAsync(Entities, cancellationToken);
    }
}
