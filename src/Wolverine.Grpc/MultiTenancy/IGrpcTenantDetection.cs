using Grpc.Core;

namespace Wolverine.Grpc.MultiTenancy;

#region sample_igrpctenantdetection

/// <summary>
///     Used to create new strategies to detect the tenant id from the
///     <see cref="ServerCallContext"/> of the current gRPC call. The gRPC counterpart to
///     Wolverine.Http's <c>ITenantDetection</c>.
/// </summary>
public interface IGrpcTenantDetection
{
    /// <summary>
    ///     This method can return the actual tenant id or null to represent "not found"
    /// </summary>
    /// <param name="context">The server call context of the current gRPC call</param>
    ValueTask<string?> DetectTenant(ServerCallContext context);
}

#endregion

/// <summary>
///     Optional, synchronous variant of <see cref="IGrpcTenantDetection"/> for strategies that
///     never need to await anything. All built-in strategies implement both.
/// </summary>
public interface ISynchronousGrpcTenantDetection
{
    string? DetectTenantSynchronously(ServerCallContext context);
}
