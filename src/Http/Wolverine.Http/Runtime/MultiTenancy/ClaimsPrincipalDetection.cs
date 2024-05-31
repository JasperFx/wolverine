using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class ClaimsPrincipalDetection : ITenantDetection, ISynchronousTenantDetection
{
    private readonly string _claimType;

    public ClaimsPrincipalDetection(string claimType)
    {
        _claimType = claimType;
    }

    public string? DetectTenantSynchronously(HttpContext context)
    {
        var principal = context.User;
        var claim = principal.Claims.FirstOrDefault(x => x.Type == _claimType);

        return claim?.Value?.Trim();
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext) 
        => new(DetectTenantSynchronously(httpContext));

    public override string ToString()
    {
        return $"Tenant Id is value of claim '{_claimType}'";
    }
}