namespace Wolverine.AzureBlobStorage;

/// <summary>
/// What Wolverine knows about a document when it asks a mapping's
/// <see cref="BlobDocumentMapping.BlobNameFor" /> for its blob name.
/// </summary>
public readonly struct BlobNameContext
{
    public BlobNameContext(Type entityType, object id, string? tenantId)
    {
        EntityType = entityType;
        Id = id;
        TenantId = tenantId;
    }

    public Type EntityType { get; }

    /// <summary>
    /// The identity Wolverine resolved for the document. Never null.
    /// </summary>
    public object Id { get; }

    /// <summary>
    /// The tenant in play, or null when there is none. Wolverine hands out a default-tenant sentinel
    /// rather than null for a message with no tenant; that is normalised away before a blob name
    /// function ever sees it.
    /// </summary>
    public string? TenantId { get; }

    public override string ToString()
    {
        return TenantId == null ? $"{EntityType.Name} {Id}" : $"{EntityType.Name} {Id} for tenant {TenantId}";
    }
}
