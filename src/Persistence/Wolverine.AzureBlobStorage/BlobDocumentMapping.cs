using System.Reflection;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Persistence.Sagas;

namespace Wolverine.AzureBlobStorage;

public class InvalidBlobDocumentMappingException : Exception
{
    public InvalidBlobDocumentMappingException(Type entityType, string reason)
        : base($"{entityType.FullNameInCode()} cannot be stored in Azure Blob Storage. {reason}")
    {
    }
}

/// <summary>
/// How one document type is addressed in Azure Blob Storage. Both <see cref="ContainerName" /> and
/// <see cref="BlobNameFor" /> are required — Wolverine has no default blob name layout, because the
/// identity-to-name mapping is the part only the application knows.
/// </summary>
public class BlobDocumentMapping
{
    internal BlobDocumentMapping(Type entityType)
    {
        EntityType = entityType;
    }

    public Type EntityType { get; }

    /// <summary>
    /// The container these documents live in. Wolverine never creates it.
    /// </summary>
    public string? ContainerName { get; set; }

    /// <summary>
    /// The blob name for a document. Called for every read and write of this type.
    /// </summary>
    /// <example>
    /// <code>x.BlobNameFor = ctx => $"invoices/v7/{ctx.TenantId}/{ctx.Id}.json";</code>
    /// </example>
    public Func<BlobNameContext, string>? BlobNameFor { get; set; }

    public IBlobDocumentSerializer Serializer { get; set; } = BlobDocumentSerializer.Default;

    /// <summary>
    /// The CLR type of this document's identity. Leave it unset to take the type of the document's own
    /// identity member.
    /// </summary>
    /// <remarks>
    /// A message handler binds an <c>[Entity]</c> parameter's identity by matching a message member on
    /// exact CLR type, so getting this wrong means the parameter does not bind at all.
    /// </remarks>
    public Type? IdentityType { get; set; }

    /// <summary>
    /// True when this type was registered through <c>Saga&lt;T&gt;()</c> rather than
    /// <c>Store&lt;T&gt;()</c>. Saga writes are conditional; document writes are not.
    /// </summary>
    internal bool IsSaga { get; init; }

    internal MemberInfo? IdentityMember { get; private set; }

    internal Type ResolvedIdentityType { get; private set; } = typeof(string);

    internal void Compile()
    {
        if (ContainerName.IsEmpty())
        {
            throw new InvalidBlobDocumentMappingException(EntityType,
                $"No container name was set. Set it with {registrationCall}(x => x.ContainerName = \"...\").");
        }

        if (BlobNameFor == null)
        {
            throw new InvalidBlobDocumentMappingException(EntityType,
                $"No blob name function was set. Set it with {registrationCall}(x => x.BlobNameFor = ctx => ...).");
        }

        // Passing the type as both arguments is how the shared convention is asked what identifies the
        // type itself, as InMemoryPersistenceFrameProvider does for [Entity].
        IdentityMember = SagaChain.DetermineSagaIdMember(EntityType, EntityType);
        ResolvedIdentityType = IdentityType ?? IdentityMember?.GetMemberType() ?? typeof(string);

        if (IdentityMember == null && IdentityType == null)
        {
            throw new InvalidBlobDocumentMappingException(EntityType,
                "No identity member could be found. Add an Id member, or set IdentityType explicitly.");
        }
    }

    private string registrationCall =>
        IsSaga ? $"Saga<{EntityType.NameInCode()}>" : $"Store<{EntityType.NameInCode()}>";

    internal string BlobNameForIdentity(object id, string? tenantId)
    {
        return BlobNameFor!(new BlobNameContext(EntityType, id, withoutDefaultTenant(tenantId)));
    }

    // Wolverine hands out StorageConstants.DefaultTenantId rather than null for a message with no
    // tenant. A blob name function should not have to know that sentinel exists.
    private static string? withoutDefaultTenant(string? tenantId)
    {
        return tenantId == null || tenantId == StorageConstants.DefaultTenantId ? null : tenantId;
    }

    internal string BlobNameForEntity(object entity, string? tenantId)
    {
        if (IdentityMember == null)
        {
            throw new InvalidBlobDocumentMappingException(EntityType,
                "It has no identity member, so Wolverine cannot work out the blob name of an instance. Writes need one even when reads take their identity from the message or route.");
        }

        var id = readIdentity(IdentityMember, entity)
                 ?? throw new InvalidOperationException(
                     $"The {IdentityMember.Name} of this {EntityType.FullNameInCode()} is null, so it has no blob name in container {ContainerName}.");

        return BlobNameForIdentity(id, tenantId);
    }

    private static object? readIdentity(MemberInfo member, object entity)
    {
        return member switch
        {
            PropertyInfo property => property.GetValue(entity),
            FieldInfo field => field.GetValue(entity),
            _ => null
        };
    }
}
