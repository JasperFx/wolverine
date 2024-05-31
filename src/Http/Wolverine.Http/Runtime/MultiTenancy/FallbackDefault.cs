using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class FallbackDefault : ITenantDetection
{
    public string TenantId { get; }

    public FallbackDefault(string tenantId)
    {
        TenantId = tenantId;
    }

    public ValueTask<string?> DetectTenant(HttpContext context)
    {
        return new ValueTask<string?>(TenantId);
    }
}