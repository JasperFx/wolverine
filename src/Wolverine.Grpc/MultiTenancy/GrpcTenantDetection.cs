using Grpc.Core;

namespace Wolverine.Grpc.MultiTenancy;

/// <summary>
///     Runtime helpers called from Wolverine's generated gRPC service wrappers for server-side
///     tenant id detection. Public only so generated code can reach them — not intended to be
///     called from application code.
/// </summary>
public static class GrpcTenantDetection
{
    public const string NoMandatoryTenantIdCouldBeDetectedForThisGrpcCall =
        "No mandatory tenant id could be detected for this gRPC call";

    /// <summary>
    ///     Throws an <see cref="RpcException"/> with status <see cref="StatusCode.InvalidArgument"/>
    ///     when no tenant id was detected and <c>TenantId.AssertExists()</c> was configured.
    ///     <c>InvalidArgument</c> is the gRPC mapping of the 400 Bad Request that Wolverine.Http's
    ///     <c>AssertExists()</c> writes for the same condition.
    /// </summary>
    public static void AssertTenantIdExists(string? tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                NoMandatoryTenantIdCouldBeDetectedForThisGrpcCall));
        }
    }

    /// <summary>
    ///     Applies a detected tenant id to the scoped <see cref="IMessageContext"/> resolved from
    ///     the current request's service provider. Used by generated wrappers for hand-written
    ///     services, which delegate to user code that resolves its own <see cref="IMessageBus"/>
    ///     rather than receiving one from the wrapper. No-ops when the tenant id is empty or no
    ///     Wolverine scope is active.
    /// </summary>
    public static void TryApplyToAmbientContext(IServiceProvider services, string? tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return;
        }

        if (services.GetService(typeof(IMessageContext)) is IMessageContext context)
        {
            context.TenantId = tenantId;
        }
    }
}
