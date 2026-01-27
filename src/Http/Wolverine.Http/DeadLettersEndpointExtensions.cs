using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;

namespace Wolverine.Http;

public class DeadLetterEnvelopeIdsRequest
{
    public Guid[] Ids { get; set; }
    public string? TenantId { get; set; }
}

public static class DeadLettersEndpointExtensions
{
    /// <summary>
    ///     Add endpoints to manage the Wolverine database-backed deal letter queue for this
    ///     application.
    /// </summary>
    /// <param name="groupUrlPrefix">
    ///     Optionally override the group Url prefix for these endpoints. The default is
    ///     "/dead-letters"
    /// </param>
    public static RouteGroupBuilder MapDeadLettersEndpoints(this IEndpointRouteBuilder endpoints,
        string? groupUrlPrefix = "/dead-letters")
    {
        if (groupUrlPrefix.IsEmpty())
        {
            throw new ArgumentNullException(nameof(groupUrlPrefix), "Cannot be empty or null");
        }

        var deadlettersGroup = endpoints.MapGroup(groupUrlPrefix);

        deadlettersGroup.MapPost("/",
            async (DeadLetterEnvelopeGetRequest request, MessageStoreCollection stores,
                CancellationToken cancellation) =>
            {
                return await stores.FetchDeadLetterEnvelopesAsync(request, cancellation);
            });

        deadlettersGroup.MapPost("/replay", (DeadLetterEnvelopeIdsRequest request, IWolverineRuntime runtime) =>
        {
            if (request.TenantId.IsEmpty())
            {
                return runtime.Stores.ReplayDeadLettersAsync(request.Ids);
            }

            return runtime.Stores.ReplayDeadLettersAsync(request.TenantId, request.Ids);
        });


        deadlettersGroup.MapDelete("/", ([FromBody] DeadLetterEnvelopeIdsRequest request, IWolverineRuntime runtime) =>
        {
            if (request.TenantId.IsEmpty())
            {
                return runtime.Stores.DiscardDeadLettersAsync(request.Ids);
            }

            return runtime.Stores.DiscardDeadLettersAsync(request.TenantId, request.Ids);
        });

        return deadlettersGroup;
    }
}