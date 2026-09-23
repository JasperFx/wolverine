using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.Runtime;

/// <summary>
/// GH-4516. Maps an <b>unknown</b> tenant id onto a 404 ProblemDetails instead of letting it escape as a 500.
///
/// <para>
/// A <b>missing</b> mandatory tenant id is already handled: <c>[RequiresTenant]</c> stops the request with a
/// 400 ProblemDetails through <c>HttpHandler.WriteTenantIdNotFound</c>. But a tenant id that is present on
/// the request and simply has no database or registration behind it throws
/// <see cref="UnknownTenantIdException"/> from the store or from Wolverine's own tenant sources, which
/// Wolverine.Http did not catch -- so the client got a 500 for what is a client-side error.
/// </para>
///
/// <para>
/// 404 rather than 400 on purpose: it reads as "the thing you addressed does not exist", and it keeps 400
/// meaning "you did not say which tenant", which is the distinction the two failures actually have.
/// </para>
/// </summary>
public static class UnknownTenantMiddleware
{
    public static ProblemDetails OnException(UnknownTenantIdException ex)
    {
        return TenancyProblemMapping.ToUnknownTenantProblemDetails(ex);
    }
}

/// <summary>
/// GH-4516. The one place the tenancy ProblemDetails are shaped, so they cannot drift from each other or
/// from <see cref="ConflictMapping"/>.
/// </summary>
public static class TenancyProblemMapping
{
    public const int UnknownTenantStatusCode = StatusCodes.Status404NotFound;

    public static ProblemDetails ToUnknownTenantProblemDetails(Exception ex)
    {
        return new ProblemDetails
        {
            Status = UnknownTenantStatusCode,
            Title = "Unknown tenant",
            Detail = ex.Message
        };
    }

    /// <summary>
    /// The default set of chains this mapping applies to: the ones that can actually resolve a tenant, and
    /// so are the only ones that can fail to. A chain that is explicitly
    /// <see cref="Wolverine.Http.TenancyMode.None"/> -- or that never declared a tenancy mode at all --
    /// has nothing to report.
    /// </summary>
    public static bool IsTenanted(HttpChain chain)
    {
        return chain.TenancyMode is TenancyMode.Required or TenancyMode.Maybe;
    }
}

/// <summary>
/// GH-4516. Stamps <c>ProducesProblem(404)</c> on the tenanted chains, so the OpenAPI document advertises
/// the unknown-tenant response the same way <see cref="ConflictProblemPolicy"/> advertises the conflict.
/// </summary>
public class UnknownTenantProblemPolicy : IHttpPolicy
{
    private readonly Func<HttpChain, bool> _filter;

    public UnknownTenantProblemPolicy(Func<HttpChain, bool> filter)
    {
        _filter = filter;
    }

    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(_filter))
        {
            chain.Metadata.ProducesProblem(TenancyProblemMapping.UnknownTenantStatusCode);
        }
    }
}
