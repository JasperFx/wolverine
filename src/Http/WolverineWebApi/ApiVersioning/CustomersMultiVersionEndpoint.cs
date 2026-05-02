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
    // The explicit OperationId is the short type-name form. CloneForVersion appends a
    // sanitised "_v{major}_{minor}" suffix to each clone so the resulting endpoint names
    // (e.g. CustomersMultiVersionEndpoint.Get_v1, CustomersMultiVersionEndpoint.Get_v2,
    // CustomersMultiVersionEndpoint.Get_v3) stay globally unique without any per-clone setup.
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
    [WolverineGet("/customers", OperationId = "CustomersV4AttributeDeprecatedEndpoint.Get")]
    public static CustomersResponse Get() => new(["v4-alice"]);
}

public record CustomersResponse(IReadOnlyList<string> Names);
