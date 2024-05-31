using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

#region sample_ArgumentDetection

internal class ArgumentDetection : ITenantDetection, ISynchronousTenantDetection
{
    private readonly string _argumentName;

    public ArgumentDetection(string argumentName)
    {
        _argumentName = argumentName;
    }
    
    

    public ValueTask<string?> DetectTenant(HttpContext httpContext) 
        => new(DetectTenantSynchronously(httpContext));

    public override string ToString()
    {
        return $"Tenant Id is route argument named '{_argumentName}'";
    }

    public string? DetectTenantSynchronously(HttpContext context)
    {
        return context.Request.RouteValues.TryGetValue(_argumentName, out var value)
            ? value?.ToString()
            : null;
    }
}

#endregion