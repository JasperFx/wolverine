using System.Diagnostics.CodeAnalysis;
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
