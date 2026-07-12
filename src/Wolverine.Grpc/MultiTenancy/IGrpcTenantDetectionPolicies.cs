namespace Wolverine.Grpc.MultiTenancy;

/// <summary>
///     Configuration API for server-side tenant id detection on Wolverine-managed gRPC services,
///     exposed as <see cref="WolverineGrpcOptions.TenantId"/>. The gRPC counterpart to
///     Wolverine.Http's <c>ITenantDetectionPolicies</c>. Strategies are tried in registration
///     order; the first non-empty tenant id wins. The detected value is written to the
///     <c>tenantId</c> code-generation variable (so Marten/Polecat session-opening frames can
///     consume it) and applied to the scoped <see cref="IMessageBus"/> before the RPC forwards
///     to any Wolverine handler.
/// </summary>
public interface IGrpcTenantDetectionPolicies
{
    /// <summary>
    ///     Try to detect the tenant id from a request metadata header on the inbound call
    ///     (<c>ServerCallContext.RequestHeaders</c>). Header name matching is case-insensitive.
    /// </summary>
    /// <param name="headerKey">The metadata header name, e.g. "tenant-id"</param>
    void IsRequestHeaderValue(string headerKey);

    /// <summary>
    ///     Try to detect the tenant id from the authenticated <c>ClaimsPrincipal</c> for the
    ///     current call (read from the underlying ASP.NET Core <c>HttpContext.User</c>, which is
    ///     where grpc-aspnetcore surfaces authentication results).
    /// </summary>
    /// <param name="claimType">The claim type to read the tenant id from</param>
    void IsClaimTypeNamed(string claimType);

    /// <summary>
    ///     Assert that the tenant id was successfully detected. When no tenant id is found the
    ///     generated service throws an <c>RpcException</c> with status
    ///     <c>InvalidArgument</c> — the gRPC equivalent of the 400 Bad Request that
    ///     Wolverine.Http's <c>AssertExists()</c> returns.
    /// </summary>
    void AssertExists();

    /// <summary>
    ///     Fallback tenant id used when no earlier strategy found one. Register this last —
    ///     strategies run in order and this one always "succeeds".
    /// </summary>
    /// <param name="defaultTenantId">The fallback tenant id</param>
    void DefaultIs(string defaultTenantId);

    /// <summary>
    ///     Register a custom tenant detection strategy instance.
    /// </summary>
    /// <param name="detection">The strategy to add</param>
    void DetectWith(IGrpcTenantDetection detection);

    /// <summary>
    ///     Register a custom tenant detection strategy by type. The instance is built from your
    ///     application container during bootstrapping (with singleton scoping), immediately before
    ///     code generation runs.
    /// </summary>
    /// <typeparam name="T">The custom strategy type</typeparam>
    void DetectWith<T>() where T : IGrpcTenantDetection;
}
