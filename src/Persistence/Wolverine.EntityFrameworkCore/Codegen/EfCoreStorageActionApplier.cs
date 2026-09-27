using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Wolverine.Persistence;

namespace Wolverine.EntityFrameworkCore.Codegen;

// AOT note (#2746): StoreAsync's existence check goes through DbContext.FindAsync(Type, object[]) and
// reads the primary key off the entity with the EF Core model's own PropertyInfo handles. Both the
// entity CLR types and their key members are rooted by the DbContext model itself -- the same
// justification ConjoinedTenancyModelCustomizer's query filters carry, and the same chunk P pattern as
// EFCorePersistenceFrameProvider, whose codegen closes these helpers over the runtime entity type.
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "Primary key members are reached through the EF Core model, which roots the entity CLR types. See AOT guide / #2755.")]
[UnconditionalSuppressMessage("Trimming", "IL2067",
    Justification = "A key property's ClrType comes from the EF Core model and is rooted by it; Activator only needs the default value of a primitive key type. See AOT guide / #2755.")]
[UnconditionalSuppressMessage("Trimming", "IL2087",
    Justification = "TEntity is closed at codegen time over an entity type mapped in a registered DbContext, so the model already roots it. See AOT guide / #2755.")]
[UnconditionalSuppressMessage("Trimming", "IL2091",
    Justification = "TEntity is closed at codegen time over an entity type mapped in a registered DbContext, so the model already roots it. See AOT guide / #2755.")]
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = "GH-4629 batch existence check: List<TKey> closed over a primary key type from the EF Core model, which roots it. See AOT guide / #2755.")]
public static class EfCoreStorageActionApplier
{
    public static async Task ApplyAction<TEntity, TDbContext>(TDbContext context, IStorageAction<TEntity> action) where TDbContext : DbContext
    {
        if (action.Entity == null) return;

        switch (action.Action)
        {
            case StorageAction.Delete:
                context.Remove(action.Entity);
                break;
            case StorageAction.Insert:
                await context.AddAsync(action.Entity);
                break;
            case StorageAction.Store:
                await StoreAsync(context, action.Entity);
                break;
            case StorageAction.Update:
                await UpdateAsync(context, action.Entity);
                break;

        }
    }

    /// <summary>
    ///     GH-4629. Apply a whole <see cref="UnitOfWork{T}" /> at once.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     The per-action loop this replaces paid two costs that only show up in bulk. Every
    ///     <c>Store</c> was a <c>FindAsync</c> round trip of its own, so a unit of work of a thousand
    ///     entities was a thousand queries; and <c>isTracked</c> walked the whole change tracker on
    ///     every call, which is quadratic in the size of the unit of work all by itself. Here the
    ///     change tracker is read once into a set, and every <c>Store</c> whose key is set is resolved
    ///     by a single <c>WHERE key IN (...)</c>.
    ///     </para>
    ///     <para>
    ///     A unit of work of one action is deliberately handed straight to <see cref="ApplyAction{TEntity,TDbContext}" />:
    ///     there is nothing to batch, and the single-entity path stays byte for byte what it was.
    ///     So does the composite-key and shadow-key case, which cannot be expressed as one <c>IN</c>
    ///     query and falls back to <see cref="StoreAsync{TEntity,TDbContext}" /> per entity.
    ///     </para>
    /// </remarks>
    public static async Task ApplyActionsAsync<TEntity, TDbContext>(TDbContext context,
        IEnumerable<IStorageAction<TEntity>> actions, CancellationToken cancellationToken)
        where TDbContext : DbContext where TEntity : class
    {
        var list = actions as IReadOnlyList<IStorageAction<TEntity>> ?? actions.ToList();
        if (list.Count == 0) return;

        if (list.Count == 1)
        {
            await ApplyAction(context, list[0]).ConfigureAwait(false);
            return;
        }

        var tracked = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var entry in context.ChangeTracker.Entries())
        {
            tracked.Add(entry.Entity);
        }

        var batch = await StoreBatch<TEntity>.BuildAsync(context, list, tracked, cancellationToken)
            .ConfigureAwait(false);

        foreach (var action in list)
        {
            if (action.Entity == null) continue;

            switch (action.Action)
            {
                case StorageAction.Delete:
                    context.Remove(action.Entity);
                    break;

                case StorageAction.Insert:
                    await context.AddAsync(action.Entity, cancellationToken).ConfigureAwait(false);
                    break;

                case StorageAction.Update:
                    if (!tracked.Contains(action.Entity))
                    {
                        context.Update(action.Entity);
                    }

                    break;

                case StorageAction.Store:
                    if (tracked.Contains(action.Entity)) break;
                    await batch.StoreAsync(context, action.Entity).ConfigureAwait(false);
                    break;
            }

            tracked.Add(action.Entity);
        }
    }

    /// <summary>
    ///     The one existence query behind <see cref="ApplyActionsAsync{TEntity,TDbContext}" />, and the
    ///     lookup of what it found.
    /// </summary>
    private sealed class StoreBatch<TEntity> where TEntity : class
    {
        private readonly Dictionary<object, object> _existing = new();
        private PropertyInfo? _key;

        /// <summary>
        ///     False when the entity's identity cannot be expressed as one comparable column -- a
        ///     composite key, or a shadow key with no CLR property. Those fall back to the unchanged
        ///     per-entity <see cref="StoreAsync{TEntity,TDbContext}" />.
        /// </summary>
        private bool usable => _key != null;

        public static async Task<StoreBatch<TEntity>> BuildAsync(DbContext context,
            IReadOnlyList<IStorageAction<TEntity>> actions, HashSet<object> tracked,
            CancellationToken cancellationToken)
        {
            var batch = new StoreBatch<TEntity>();

            var stores = actions
                .Where(x => x.Action == StorageAction.Store && x.Entity != null && !tracked.Contains(x.Entity))
                .ToArray();

            if (stores.Length == 0) return batch;

            var primaryKey = context.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey();
            if (primaryKey is not { Properties.Count: 1 }) return batch;

            var property = primaryKey.Properties[0].PropertyInfo;
            if (property == null) return batch;

            batch._key = property;

            var keyType = property.PropertyType;
            var listType = typeof(List<>).MakeGenericType(keyType);
            var keys = (IList)Activator.CreateInstance(listType)!;
            var seen = new HashSet<object>();

            foreach (var action in stores)
            {
                var value = property.GetValue(action.Entity);
                if (value == null || value.Equals(defaultValueOf(keyType))) continue;
                if (seen.Add(value)) keys.Add(value);
            }

            if (keys.Count == 0) return batch;

            // One WHERE key IN (...) for the entire unit of work. The rows come back TRACKED on
            // purpose: that is what FindAsync did per entity, and it is what lets CurrentValues
            // .SetValues below carry correct original values into the UPDATE -- which matters for a
            // concurrency token like the conjoined tenant_id (GH-4612).
            var parameter = Expression.Parameter(typeof(TEntity), "x");
            var contains = listType.GetMethod(nameof(List<object>.Contains), [keyType])!;
            var predicate = Expression.Lambda<Func<TEntity, bool>>(
                Expression.Call(Expression.Constant(keys, listType), contains,
                    Expression.Property(parameter, property)), parameter);

            var found = await context.Set<TEntity>().Where(predicate).ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in found)
            {
                var value = property.GetValue(row);
                if (value != null)
                {
                    batch._existing[value] = row;
                }
            }

            return batch;
        }

        public async Task StoreAsync<TDbContext>(TDbContext context, TEntity entity) where TDbContext : DbContext
        {
            if (!usable)
            {
                await EfCoreStorageActionApplier.StoreAsync(context, entity).ConfigureAwait(false);
                return;
            }

            var value = _key!.GetValue(entity);
            if (value == null || value.Equals(defaultValueOf(_key.PropertyType)))
            {
                // No key to look a row up by, so there is no row. Update() is what the single-entity
                // path does here, and EF marks an entity with an unset key Added rather than Modified.
                context.Update(entity);
                return;
            }

            if (_existing.TryGetValue(value, out var existing))
            {
                if (!ReferenceEquals(existing, entity))
                {
                    context.Entry(existing).CurrentValues.SetValues(entity);
                }

                return;
            }

            await context.AddAsync(entity).ConfigureAwait(false);

            // A second Store of the same key in the same unit of work must find this one, exactly as
            // FindAsync used to find it in the change tracker.
            _existing[value] = entity;
        }
    }

    /// <summary>
    /// GH-4613. Attach an entity the handler returned from an <c>Update&lt;T&gt;</c> or an
    /// <c>IStorageAction&lt;T&gt;</c> so that <c>SaveChangesAsync</c> writes it.
    /// </summary>
    /// <remarks>
    /// An entity the DbContext is already tracking needs nothing: change tracking has the modifications
    /// and <c>Update()</c> would only widen the UPDATE to every column. An entity it is NOT tracking --
    /// one built from the message, taken from a request body, or read with <c>AsNoTracking()</c> -- is
    /// invisible to <c>SaveChangesAsync</c> until it is attached, which is the whole of the bug: the
    /// generated code contained a comment where this call belongs and the write silently did nothing.
    /// </remarks>
    public static Task UpdateAsync<TEntity, TDbContext>(TDbContext context, TEntity entity)
        where TDbContext : DbContext
    {
        if (entity is null) return Task.CompletedTask;

        if (!isTracked(context, entity))
        {
            context.Update(entity);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// GH-4613. A real upsert for <c>Store&lt;T&gt;</c>, which is what the return type means on every
    /// other Wolverine persistence provider and what the EF Core operations guide documents.
    /// </summary>
    /// <remarks>
    /// EF Core has no upsert statement, and <c>DbContext.Update()</c> is update-only: it marks a brand new
    /// entity Modified, so the save affected zero rows and threw <see cref="DbUpdateConcurrencyException" />
    /// rather than inserting. The existence check is therefore a genuine round trip and not an accident --
    /// there is no way to ask the database to decide for us. A tracked entity short circuits it entirely,
    /// and so does an entity whose key is still unset, which can only be an insert.
    /// </remarks>
    public static async Task StoreAsync<TEntity, TDbContext>(TDbContext context, TEntity entity)
        where TDbContext : DbContext
    {
        if (entity is null) return;

        // Already tracked -- whatever state it is in, SaveChanges already knows what to do with it
        if (isTracked(context, entity)) return;

        if (!tryReadPrimaryKey(context, entity, out var keyValues))
        {
            // No readable primary key (a shadow or composite-with-shadow key). Update() is the best
            // available answer and matches the pre-GH-4613 behavior.
            context.Update(entity);
            return;
        }

        var existing = await context.FindAsync(typeof(TEntity), keyValues);
        if (existing == null)
        {
            await context.AddAsync(entity);
        }
        else if (!ReferenceEquals(existing, entity))
        {
            // FindAsync attached the stored row; copy the caller's values onto it so the UPDATE carries
            // the correct original values (which matters for a concurrency token -- see the conjoined
            // multi-tenancy tenant_id token, GH-4612)
            context.Entry(existing).CurrentValues.SetValues(entity);
        }
    }

    /// <summary>
    /// The entity's primary key values, as <c>FindAsync</c> wants them. False when the key cannot be read
    /// off the CLR instance at all, or when any part of it is still unset -- an unset key means there is
    /// no row to look for.
    /// </summary>
    private static bool tryReadPrimaryKey<TEntity>(DbContext context, TEntity entity, out object[] keyValues)
    {
        keyValues = [];

        var key = context.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey();
        if (key == null) return false;

        var values = new object[key.Properties.Count];
        for (var i = 0; i < key.Properties.Count; i++)
        {
            var property = key.Properties[i].PropertyInfo;
            if (property == null) return false;

            var value = property.GetValue(entity);
            if (value == null || value.Equals(defaultValueOf(property.PropertyType))) return false;

            values[i] = value;
        }

        keyValues = values;
        return true;
    }

    private static object? defaultValueOf(Type type)
    {
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static bool isTracked<TEntity>(DbContext context, TEntity entity)
    {
        return context.ChangeTracker.Entries().Any(entry => ReferenceEquals(entry.Entity, entity));
    }
}
