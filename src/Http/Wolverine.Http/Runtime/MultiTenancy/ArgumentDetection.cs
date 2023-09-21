using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class ArgumentDetection : ITenantDetection
{
    private readonly string _argumentName;

    public ArgumentDetection(string argumentName)
    {
        _argumentName = argumentName;
    }

    public ValueTask<string?> DetectTenant(HttpContext httpContext)
    {
        return httpContext.Request.RouteValues.TryGetValue(_argumentName, out var value) 
            ? new ValueTask<string?>(value?.ToString()) 
            : ValueTask.FromResult<string?>(null);
    }

    public override string ToString()
    {
        return $"Tenant Id is route argument named '{_argumentName}'";
    }
}