using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Inserts the <see cref="ApiVersionHeaderWriter"/> call as the very first frame of every chain that
/// <see cref="ApiVersioningPolicy"/> previously flagged as needing header emission. Registered by
/// <c>MapWolverineEndpoints</c> after <c>configure</c> has run, so it executes after every
/// user-supplied policy — including <c>HttpChainFluentValidationPolicy</c> and
/// <c>HttpChainDataAnnotationsValidationPolicy</c>, both of which themselves insert short-circuiting
/// frames at index 0. Running last guarantees the writer is the first frame at request time, so its
/// <c>Response.OnStarting</c> callback is registered before any frame can <c>return;</c> out of the
/// generated handler — which is what gives validation 4xx, middleware short-circuits, and IResult
/// handler exits the same versioning headers as the success path.
/// </summary>
internal sealed class ApiVersionHeaderFinalizationPolicy : IHttpPolicy
{
    private readonly IReadOnlyCollection<HttpChain> _chainsRequiringWriter;

    public ApiVersionHeaderFinalizationPolicy(IReadOnlyCollection<HttpChain> chainsRequiringWriter)
    {
        _chainsRequiringWriter = chainsRequiringWriter;
    }

    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            if (!_chainsRequiringWriter.Contains(chain))
                continue;

            // Idempotency: leave a writer call already at index 0 alone; otherwise remove any stray
            // copy and re-insert at 0. Other policies inserting at index 0 between this run and the
            // initial step push the writer down, so we have to re-position.
            var existing = chain.Middleware.OfType<MethodCall>()
                .FirstOrDefault(c => c.HandlerType == typeof(ApiVersionHeaderWriter));

            if (existing is not null)
            {
                if (chain.Middleware.IndexOf(existing) == 0) continue;
                chain.Middleware.Remove(existing);
            }

            chain.Middleware.Insert(0, MethodCall.For<ApiVersionHeaderWriter>(x => x.WriteAsync(null!)));
        }
    }
}
