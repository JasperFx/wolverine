using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime.MultiTenancy;

#region sample_ITenantDetection

/// <summary>
/// Used to create new strategies to detect the tenant id from an HttpContext
/// for the current request
/// </summary>
public interface ITenantDetection
{
    /// <summary>
    /// This method can return the actual tenant id or null to represent "not found"
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public ValueTask<string?> DetectTenant(HttpContext context);
}

#endregion

public interface ISynchronousTenantDetection
{
    string? DetectTenantSynchronously(HttpContext context);
}