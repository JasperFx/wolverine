using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class QueryStringDetection : ITenantDetection
{
    private readonly string _key;

    public QueryStringDetection(string key)
    {
        _key = key;
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext)
    {
        return httpContext.Request.Query.TryGetValue(_key, out var value)
            ? ValueTask.FromResult<string?>(value)
            : ValueTask.FromResult<string?>(null);
    }

    public override string ToString()
    {
        return $"Tenant Id is query string value '{_key}'";
    }
}