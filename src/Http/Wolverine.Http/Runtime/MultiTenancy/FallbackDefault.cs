using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class FallbackDefault : ITenantDetection, ISynchronousTenantDetection
{
    public string TenantId { get; }

    public FallbackDefault(string tenantId)
    {
        TenantId = tenantId;
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext) 
        => new(DetectTenantSynchronously(httpContext));

    public string? DetectTenantSynchronously(HttpContext context)
    {
        return TenantId;
    }
}