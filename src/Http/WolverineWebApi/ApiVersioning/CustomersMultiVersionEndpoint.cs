using Asp.Versioning;
using Wolverine.Http;

namespace WolverineWebApi.ApiVersioning;

/// <summary>
/// Multi-version handler example. Class-level <c>[ApiVersion]</c> declares every version this
/// type serves; the single <c>Get</c> method serves all three. The Wolverine.Http startup pipeline
/// expands this class into one HTTP chain per version, each rewritten with the URL-segment prefix
/// (e.g. <c>/v1/customers</c>, <c>/v2/customers</c>, <c>/v3/customers</c>).
/// </summary>
[ApiVersion("1.0", Deprecated = true)]
[ApiVersion("2.0")]
[ApiVersion("3.0")]
public static class CustomersMultiVersionEndpoint
{
    // The explicit OperationId is retained here because WolverineWebApi is also loaded by
    // tests that DO NOT call options.UseApiVersioning(). In that mode MultiVersionExpansion
    // and ApiVersioningPolicy never run, so the per-clone auto-suffix and the SetExplicitOperationId
    // call in AttachMetadata never fire — without an explicit OperationId on the source attribute,
    // multiple chains at /customers (this class plus CustomersV4AttributeDeprecatedEndpoint) would
    // collide on the route-derived endpoint name 'GET_customers'. When versioning IS enabled, the
    // policy auto-suffixes each clone (CustomersMultiVersionEndpoint.Get_v1_0, _v2_0, _v3_0) so
    // global uniqueness is guaranteed regardless of this attribute.
    [WolverineGet("/customers", OperationId = "CustomersMultiVersionEndpoint.Get")]
    public static CustomersResponse Get() => new(["alice", "bob"]);
}

/// <summary>
/// <c>[MapToApiVersion]</c> example: the class advertises 1.0, 2.0, and 3.0; this method opts in
/// to v2.0 only. The other versions are not registered for this route.
/// </summary>
[ApiVersion("1.0")]
[ApiVersion("2.0")]
[ApiVersion("3.0")]
public static class CustomersV2OnlyEndpoint
{
    [WolverineGet("/customers/v2-only", OperationId = "CustomersV2OnlyEndpoint.Get")]
    [MapToApiVersion("2.0")]
    public static CustomersResponse Get() => new(["v2-only-alice"]);
}

/// <summary>
/// Attribute-only deprecation example. v4.0 is marked deprecated via the attribute alone — there
/// is no <c>options.Deprecate("4.0")</c> in <c>Program.cs</c>, so the <c>Deprecation</c> response
/// header on <c>/v4/customers</c> proves the per-version attribute is honoured independently of
/// the options-driven sunset / deprecation map. Used by integration tests to isolate attribute
/// behaviour from options behaviour.
/// </summary>
[ApiVersion("4.0", Deprecated = true)]
public static class CustomersV4AttributeDeprecatedEndpoint
{
    // Explicit OperationId required for the same reason documented on CustomersMultiVersionEndpoint:
    // tests that load WolverineWebApi without UseApiVersioning() must still produce unique endpoint
    // names. Both chains share GET /customers; without explicit operation IDs they collide on
    // 'GET_customers' before any policy can disambiguate them.
    [WolverineGet("/customers", OperationId = "CustomersV4AttributeDeprecatedEndpoint.Get")]
    public static CustomersResponse Get() => new(["v4-alice"]);
}

public record CustomersResponse(IReadOnlyList<string> Names);
