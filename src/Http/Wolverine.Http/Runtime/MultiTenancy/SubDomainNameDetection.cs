using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class SubDomainNameDetection : ITenantDetection, ISynchronousTenantDetection
{
    public ValueTask<string?> DetectTenant(HttpContext httpContext) 
        => new(DetectTenantSynchronously(httpContext));

    public override string ToString()
    {
        return "Tenant Id is first sub domain name of the request host";
    }

    public string? DetectTenantSynchronously(HttpContext context)
    {
        var parts = context.Request.Host.Host.Split('.');
        if (parts.Length > 1)
        {
            return parts[0];
        }

        return null;
    }
}