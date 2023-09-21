using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

public interface ITenantDetection
{
    public ValueTask<string?> DetectTenant(HttpContext context);
}