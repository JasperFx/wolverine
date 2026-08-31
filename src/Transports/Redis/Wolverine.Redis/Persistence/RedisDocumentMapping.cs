using System.Reflection;
using JasperFx;
using JasperFx.Core.Reflection;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Redis;

public class InvalidRedisMappingException : Exception
{
    public InvalidRedisMappingException(Type entityType, string reason)
        : base($"{entityType.FullNameInCode()} cannot be stored in Redis. {reason}")
    {
    }
}

/// <summary>
/// How one document or saga type is addressed in Redis. <see cref="KeyFor" /> is required — Wolverine has
/// no default key layout, because the identity-to-key mapping is the part only the application knows, and
/// a key Wolverine invented would collide with whatever else the application already keeps in the same
/// Redis instance.
/// </summary>
public class RedisDocumentMapping
{
    internal RedisDocumentMapping(Type entityType, bool isSaga)
    {
        EntityType = entityType;
        IsSaga = isSaga;
    }

    public Type EntityType { get; }

    /// <summary>
    /// True when this type was registered with <c>Saga&lt;T&gt;()</c> rather than <c>Store&lt;T&gt;()</c>.
    /// Saga writes are compare-and-swap; document writes are last-write-wins.
    /// </summary>
    public bool IsSaga { get; }

    /// <summary>
    /// The Redis key for a document. Called for every read and write of this type.
    /// </summary>
    /// <example>
    /// <code>x.KeyFor = ctx => $"invoice/{ctx.TenantId}/{ctx.Id}";</code>
    /// </example>
    public Func<RedisKeyContext, string>? KeyFor { get; set; }

    /// <summary>
    /// The numbered Redis database these documents live in. -1, the default, means the multiplexer's own
    /// default database. Ignored by Redis Cluster, which only has database 0.
    /// </summary>
    public int Database { get; set; } = -1;

    public IRedisDocumentSerializer Serializer { get; set; } = RedisDocumentSerializer.Default;

    /// <summary>
    /// Expire the key this long after each write. Null, the default, means the key never expires.
    /// </summary>
    /// <remarks>
    /// Redis applies this natively with PEXPIRE, and Wolverine re-applies it on every write — so the
    /// window slides forward from the last write rather than from the first. On a saga this is a
    /// deliberate destructor: an expired saga is simply gone, and the next message for that identity
    /// either starts a new one or fails with <see cref="UnknownSagaException" />. It is not a substitute
    /// for a timeout message, which lets the saga run code before it disappears.
    /// </remarks>
    public TimeSpan? ExpiresAfter { get; set; }

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

    internal TimeSpan? Expiry => ExpiresAfter;

    internal void Compile()
    {
        if (KeyFor == null)
        {
            var registration = IsSaga ? "Saga" : "Store";
            throw new InvalidRedisMappingException(EntityType,
                $"No key function was set. Set it with {registration}<{EntityType.NameInCode()}>(x => x.KeyFor = ctx => ...).");
        }

        if (ExpiresAfter is { } expiry && expiry <= TimeSpan.Zero)
        {
            throw new InvalidRedisMappingException(EntityType,
                $"ExpiresAfter must be positive, but was {expiry}. Leave it null for a key that never expires.");
        }

        // Passing the type as both arguments is how the shared convention is asked what identifies the
        // type itself, as InMemoryPersistenceFrameProvider does for [Entity].
        IdentityMember = SagaChain.DetermineSagaIdMember(EntityType, EntityType);
        ResolvedIdentityType = IdentityType ?? IdentityMember?.GetMemberType() ?? typeof(string);

        if (IdentityMember == null && IdentityType == null)
        {
            throw new InvalidRedisMappingException(EntityType,
                "No identity member could be found. Add an Id member, or set IdentityType explicitly.");
        }
    }

    internal string KeyForIdentity(object id, string? tenantId)
    {
        return KeyFor!(new RedisKeyContext(EntityType, id, withoutDefaultTenant(tenantId)));
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
            throw new InvalidRedisMappingException(EntityType,
                "It has no identity member, so Wolverine cannot work out the Redis key of an instance. Writes need one even when reads take their identity from the message or route.");
        }

        var id = readIdentity(IdentityMember, entity)
                 ?? throw new InvalidOperationException(
                     $"The {IdentityMember.Name} of this {EntityType.FullNameInCode()} is null, so it has no Redis key.");

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
