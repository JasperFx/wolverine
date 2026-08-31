using System.Reflection;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Persistence.Sagas;

namespace Wolverine.AmazonS3;

public class InvalidS3DocumentMappingException : Exception
{
    public InvalidS3DocumentMappingException(Type entityType, string reason)
        : base($"{entityType.FullNameInCode()} cannot be stored in S3. {reason}")
    {
    }
}

/// <summary>
/// How one document type is addressed in S3. Both <see cref="BucketName" /> and <see cref="KeyFor" />
/// are required — Wolverine has no default key layout, because the identity-to-key mapping is the part
/// only the application knows.
/// </summary>
public class S3DocumentMapping
{
    internal S3DocumentMapping(Type entityType)
    {
        EntityType = entityType;
    }

    public Type EntityType { get; }

    /// <summary>
    /// The bucket these documents live in. Wolverine never creates it.
    /// </summary>
    public string? BucketName { get; set; }

    /// <summary>
    /// The object key for a document. Called for every read and write of this type.
    /// </summary>
    /// <example>
    /// <code>x.KeyFor = ctx => $"invoices/v7/{ctx.TenantId}/{ctx.Id}.json";</code>
    /// </example>
    public Func<S3KeyContext, string>? KeyFor { get; set; }

    public IS3DocumentSerializer Serializer { get; set; } = S3DocumentSerializer.Default;

    /// <summary>
    /// True when this type was registered through <c>Saga&lt;T&gt;()</c>. A saga is written with a
    /// conditional put -- If-None-Match on create, If-Match on update -- because a saga is a
    /// read-modify-write and two messages for one saga would otherwise silently lose an update.
    /// Ordinary documents stay last-write-wins. See GH-4160.
    /// </summary>
    internal bool IsSaga { get; init; }

    /// <summary>
    /// The CLR type of this document's identity. Leave it unset to take the type of the document's own
    /// identity member.
    /// </summary>
    /// <remarks>
    /// A message handler binds an <c>[Entity]</c> parameter's identity by matching a message member on
    /// exact CLR type, so getting this wrong means the parameter does not bind at all.
    /// </remarks>
    public Type? IdentityType { get; set; }

    internal MemberInfo? IdentityMember { get; private set; }

    internal Type ResolvedIdentityType { get; private set; } = typeof(string);

    internal void Compile()
    {
        if (BucketName.IsEmpty())
        {
            throw new InvalidS3DocumentMappingException(EntityType,
                $"No bucket name was set. Set it with Store<{EntityType.NameInCode()}>(x => x.BucketName = \"...\").");
        }

        if (KeyFor == null)
        {
            throw new InvalidS3DocumentMappingException(EntityType,
                $"No object key function was set. Set it with Store<{EntityType.NameInCode()}>(x => x.KeyFor = ctx => ...).");
        }

        // Passing the type as both arguments is how the shared convention is asked what identifies the
        // type itself, as InMemoryPersistenceFrameProvider does for [Entity].
        IdentityMember = SagaChain.DetermineSagaIdMember(EntityType, EntityType);
        ResolvedIdentityType = IdentityType ?? IdentityMember?.GetMemberType() ?? typeof(string);

        if (IdentityMember == null && IdentityType == null)
        {
            throw new InvalidS3DocumentMappingException(EntityType,
                "No identity member could be found. Add an Id member, or set IdentityType explicitly.");
        }
    }

    internal string KeyForIdentity(object id, string? tenantId)
    {
        return KeyFor!(new S3KeyContext(EntityType, id, withoutDefaultTenant(tenantId)));
    }

    // Wolverine hands out StorageConstants.DefaultTenantId rather than null for a message with no
    // tenant. A key function should not have to know that sentinel exists.
    private static string? withoutDefaultTenant(string? tenantId)
    {
        return tenantId == null || tenantId == StorageConstants.DefaultTenantId ? null : tenantId;
    }

    internal string KeyForEntity(object entity, string? tenantId)
    {
        if (IdentityMember == null)
        {
            throw new InvalidS3DocumentMappingException(EntityType,
                "It has no identity member, so Wolverine cannot work out the object key of an instance. Writes need one even when reads take their identity from the message or route.");
        }

        var id = readIdentity(IdentityMember, entity)
                 ?? throw new InvalidOperationException(
                     $"The {IdentityMember.Name} of this {EntityType.FullNameInCode()} is null, so it has no object key in bucket {BucketName}.");

        return KeyForIdentity(id, tenantId);
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
