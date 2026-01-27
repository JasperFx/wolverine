using JasperFx.Core;

namespace Wolverine.Runtime.Partitioning;

internal class TenantGroupingRule : IGroupingRule
{
    // TODO -- possibly correct the tenant id casing?
    
    public bool TryFindIdentity(Envelope envelope, out string groupId)
    {
        if (envelope.TenantId.IsNotEmpty())
        {
            groupId = envelope.TenantId;
            return true;
        }

        groupId = null!;
        return false;
    }
}