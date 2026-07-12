using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Grpc.MultiTenancy;

/// <summary>
///     Detects the tenant id from the authenticated <c>ClaimsPrincipal</c> of the current call.
///     grpc-aspnetcore surfaces authentication results on the underlying ASP.NET Core
///     <see cref="HttpContext.User"/> (the <c>ServerCallContext.AuthContext</c> only carries peer
///     certificate properties), so this reads <c>context.GetHttpContext().User</c> — the same
///     source Wolverine.Http's claim detection uses.
/// </summary>
internal class ClaimsPrincipalDetection : IGrpcTenantDetection, ISynchronousGrpcTenantDetection
{
    private readonly string _claimType;

    public ClaimsPrincipalDetection(string claimType)
    {
        _claimType = claimType;
    }

    public ValueTask<string?> DetectTenant(ServerCallContext context)
        => new(DetectTenantSynchronously(context));

    public string? DetectTenantSynchronously(ServerCallContext context)
    {
        var principal = context.GetHttpContext()?.User;
        var claim = principal?.Claims.FirstOrDefault(x => x.Type == _claimType);

        return claim?.Value?.Trim();
    }

    public override string ToString()
    {
        return $"Tenant Id is value of claim '{_claimType}'";
    }
}
