using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
///     GH-3542. Supplies the pinned tenant of the DbContext as the value of a partitioned saga's
///     <c>TenantId</c> key property the moment EF starts tracking the saga.
///
///     <para>
///     Every <c>ITenanted</c> property is mapped with a column default, and a column default makes
///     EF treat the property as store-generated on add. For an ordinary entity that is harmless --
///     EF just leaves the column out of the INSERT when the value is unset, and
///     <see cref="TenantStampingInterceptor" /> stamps the real tenant before the INSERT is built.
///     Once the property is part of the PRIMARY KEY, EF cannot track the entity without a key value,
///     so it hands a store-generated string key a <b>temporary GUID</b> at Add() time. By the time the
///     interceptor runs that GUID is sitting in TenantId, does not match the context's tenant, and
///     the save is rejected as a cross-tenant write naming a GUID as the tenant. The GUID was never
///     the saga id, and no Wolverine code ever wrote it: it is EF's placeholder key.
///     </para>
///
///     <para>
///     Replacing EF's generator with this one keeps the key store-generated in EF's eyes -- so the
///     user's handler still never has to set TenantId -- while making the generated value the ONE
///     value the interceptor would have stamped anyway. A saga whose TenantId was set explicitly
///     keeps it (EF only generates for an unset property), and the interceptor still refuses it if
///     it names another tenant.
///     </para>
/// </summary>
internal class ConjoinedTenantIdValueGenerator : ValueGenerator<string>
{
    public override bool GeneratesTemporaryValues => false;

    public override string Next(EntityEntry entry)
    {
        return ConjoinedTenancy.TenantIdOf(entry.Context);
    }
}
