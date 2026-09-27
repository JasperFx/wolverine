using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
///     Thrown when a conjoined-multi-tenant DbContext tries to insert, update, or delete
///     an ITenanted entity that belongs to a different tenant than the tenant the
///     DbContext is scoped to. This is the write-side counterpart of the tenant global
///     query filter and matches Marten's conjoined tenancy session semantics -- a session
///     only ever writes its own tenant's data
/// </summary>
public class CrossTenantWriteException : InvalidOperationException
{
    public CrossTenantWriteException(Type entityType, string? entityTenantId, string contextTenantId,
        EntityState state) : base(
        $"Cannot apply a '{state}' change to an entity of type {entityType.FullName} belonging to tenant '{entityTenantId}' through a DbContext scoped to tenant '{contextTenantId}'. A conjoined multi-tenanted DbContext can only write data for its own tenant.")
    {
        EntityType = entityType;
        EntityTenantId = entityTenantId;
        ContextTenantId = contextTenantId;
        State = state;
    }

    /// <summary>
    ///     GH-4629. The refusal of a SET-BASED write, which is turned away before it runs and so has
    ///     no entity -- and no other tenant id -- to name. All there is to report is what the
    ///     statement asked to do and which tenant asked.
    /// </summary>
    public CrossTenantWriteException(Type entityType, string contextTenantId, string reason) : base(
        $"Refusing a set-based write against {entityType.FullName} through a DbContext scoped to tenant '{contextTenantId}'. {reason}")
    {
        EntityType = entityType;
        ContextTenantId = contextTenantId;
        State = EntityState.Modified;
    }

    public Type EntityType { get; }
    public string? EntityTenantId { get; }
    public string ContextTenantId { get; }
    public EntityState State { get; }
}
