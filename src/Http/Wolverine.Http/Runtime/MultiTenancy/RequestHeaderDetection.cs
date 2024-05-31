using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class RequestHeaderDetection : ITenantDetection, ISynchronousTenantDetection
{
    private readonly string _headerName;

    public RequestHeaderDetection(string headerName)
    {
        _headerName = headerName;
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext) 
        => new(DetectTenantSynchronously(httpContext));

    public override string ToString()
    {
        return $"Tenant Id is request header '{_headerName}'";
    }

    public string? DetectTenantSynchronously(HttpContext context)
    {
        return context.Request.Headers.TryGetValue(_headerName, out var value)
            ? value.FirstOrDefault()
            : null;
    }
}