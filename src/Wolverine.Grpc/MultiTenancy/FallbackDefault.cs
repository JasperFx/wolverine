using Grpc.Core;

namespace Wolverine.Grpc.MultiTenancy;

/// <summary>
///     Terminal detection strategy that always returns a fixed tenant id. Registered by
///     <see cref="IGrpcTenantDetectionPolicies.DefaultIs"/> as the fallback when no earlier
///     strategy detected a tenant.
/// </summary>
internal class FallbackDefault : IGrpcTenantDetection, ISynchronousGrpcTenantDetection
{
    public string TenantId { get; }

    public FallbackDefault(string tenantId)
    {
        TenantId = tenantId;
    }

    public ValueTask<string?> DetectTenant(ServerCallContext context)
        => new(DetectTenantSynchronously(context));

    public string? DetectTenantSynchronously(ServerCallContext context)
    {
        return TenantId;
    }

    public override string ToString()
    {
        return $"Fallback tenant id is '{TenantId}'";
    }
}
