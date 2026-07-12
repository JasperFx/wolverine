using Grpc.Core;

namespace Wolverine.Grpc.MultiTenancy;

/// <summary>
///     Detects the tenant id from an inbound request metadata header
///     (<see cref="ServerCallContext.RequestHeaders"/>). Matching is case-insensitive —
///     gRPC lowercases metadata keys on the wire, but callers may register the header
///     name in any casing.
/// </summary>
internal class MetadataHeaderDetection : IGrpcTenantDetection, ISynchronousGrpcTenantDetection
{
    private readonly string _headerName;

    public MetadataHeaderDetection(string headerName)
    {
        _headerName = headerName;
    }

    public ValueTask<string?> DetectTenant(ServerCallContext context)
        => new(DetectTenantSynchronously(context));

    public string? DetectTenantSynchronously(ServerCallContext context)
    {
        foreach (var entry in context.RequestHeaders)
        {
            if (!entry.IsBinary && string.Equals(entry.Key, _headerName, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }

    public override string ToString()
    {
        return $"Tenant Id is request metadata header '{_headerName}'";
    }
}
