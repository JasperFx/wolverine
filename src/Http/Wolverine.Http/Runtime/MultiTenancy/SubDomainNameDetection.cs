using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class SubDomainNameDetection : ITenantDetection
{
    public ValueTask<string?> DetectTenant(HttpContext httpContext)
    {
        var parts = httpContext.Request.Host.Host.Split('.');
        if (parts.Length > 1)
        {
            return ValueTask.FromResult<string?>(parts[0]);
        }

        return ValueTask.FromResult<string?>(null);
    }

    public override string ToString()
    {
        return "Tenant Id is first sub domain name of the request host";
    }
}