using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class ClaimsPrincipalDetection : ITenantDetection
{
    private readonly string _claimType;

    public ClaimsPrincipalDetection(string claimType)
    {
        _claimType = claimType;
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext)
    {
        var principal = httpContext.User;
        var claim = principal.Claims.FirstOrDefault(x => x.Type == _claimType);

        return claim == null ? ValueTask.FromResult<string?>(null) : ValueTask.FromResult<string?>(claim.Value);
    }

    public override string ToString()
    {
        return $"Tenant Id is value of claim '{_claimType}'";
    }
}