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
        $"Cannot {state.ToString().ToLowerInvariant()} entity of type {entityType.FullName} belonging to tenant '{entityTenantId}' through a DbContext scoped to tenant '{contextTenantId}'. A conjoined multi-tenanted DbContext can only write data for its own tenant.")
    {
        EntityType = entityType;
        EntityTenantId = entityTenantId;
        ContextTenantId = contextTenantId;
        State = state;
    }

    public Type EntityType { get; }
    public string? EntityTenantId { get; }
    public string ContextTenantId { get; }
    public EntityState State { get; }
}
