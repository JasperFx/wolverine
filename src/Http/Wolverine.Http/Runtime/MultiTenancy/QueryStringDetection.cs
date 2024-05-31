using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class QueryStringDetection : ITenantDetection, ISynchronousTenantDetection
{
    private readonly string _key;

    public QueryStringDetection(string key)
    {
        _key = key;
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext) 
        => new(DetectTenantSynchronously(httpContext));

    public override string ToString()
    {
        return $"Tenant Id is query string value '{_key}'";
    }

    public string? DetectTenantSynchronously(HttpContext context)
    {
        return context.Request.Query.TryGetValue(_key, out var value)
            ? value.FirstOrDefault()
            : null;
    }
}