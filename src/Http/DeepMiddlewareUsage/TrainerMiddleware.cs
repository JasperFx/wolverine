using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace DeepMiddlewareUsage;

public static class TrainerMiddleware
{
    public static async Task<(Trainer? trainer, ProblemDetails)> LoadAsync(
        UserId userId,
        IDocumentSession session,
        CancellationToken ct,
        HttpContext context)
    {
        if (userId.Id == Guid.Empty)
            return (null, WolverineContinue.NoProblems);

        var trainer = await session.LoadAsync<Trainer>(userId.Id, ct);

        bool allowAnonymous = context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() != null;
        if (trainer == null && !allowAnonymous)
            return (null, new ProblemDetails
            {
                Title = "Unauthorized",
                Status = StatusCodes.Status401Unauthorized
            });

        return (trainer, WolverineContinue.NoProblems);
    }
}

internal class RequestTrainerPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (HttpChain chain in chains)
        {
            Type[] serviceDependencies = chain.ServiceDependencies(container, Type.EmptyTypes).ToArray();
            if (serviceDependencies.Contains(typeof(Trainer)))
            {
                chain.Middleware.Add(new MethodCall(typeof(TrainerMiddleware), nameof(TrainerMiddleware.LoadAsync)));
            }
        }
    }
}