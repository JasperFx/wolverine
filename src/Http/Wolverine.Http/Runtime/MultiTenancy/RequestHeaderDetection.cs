using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class RequestHeaderDetection : ITenantDetection
{
    private readonly string _headerName;

    public RequestHeaderDetection(string headerName)
    {
        _headerName = headerName;
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext)
    {
        return httpContext.Request.Headers.TryGetValue(_headerName, out var value)
            ? ValueTask.FromResult<string?>(value)
            : ValueTask.FromResult<string?>(null);
    }

    public override string ToString()
    {
        return $"Tenant Id is request header '{_headerName}'";
    }
}