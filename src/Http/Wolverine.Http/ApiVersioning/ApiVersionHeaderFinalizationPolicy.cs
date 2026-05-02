using System.Diagnostics;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;

namespace Wolverine.Http.ApiVersioning;

/// <summary>Inserts the <see cref="ApiVersionHeaderWriter"/> call as the first frame of every chain that <see cref="ApiVersioningPolicy"/> flagged as needing header emission.</summary>
/// <remarks>
/// Registered by <c>MapWolverineEndpoints</c> after <c>configure</c> has run, so it executes after every
/// user-supplied policy — including <c>HttpChainFluentValidationPolicy</c> and
/// <c>HttpChainDataAnnotationsValidationPolicy</c>, both of which insert short-circuiting frames at index 0.
/// Running last guarantees the writer's <c>Response.OnStarting</c> callback registers before any frame can
/// <c>return;</c> out of the generated handler — which is what gives validation 4xx, middleware short-circuits,
/// and IResult handler exits the same versioning headers as the success path.
/// </remarks>
internal sealed class ApiVersionHeaderFinalizationPolicy : IHttpPolicy
{
    private readonly IReadOnlySet<HttpChain> _chainsRequiringWriter;

    public ApiVersionHeaderFinalizationPolicy(IReadOnlySet<HttpChain> chainsRequiringWriter)
    {
        _chainsRequiringWriter = chainsRequiringWriter;
    }

    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            if (!_chainsRequiringWriter.Contains(chain))
                continue;

            // Idempotency: if a writer call is already at index 0, leave it alone.
            var existing = chain.Middleware.OfType<MethodCall>()
                .FirstOrDefault(c => c.HandlerType == typeof(ApiVersionHeaderWriter));

            if (existing is not null)
            {
                Debug.Assert(chain.Middleware.IndexOf(existing) == 0,
                    "ApiVersionHeaderWriter must be at index 0 once inserted; no other policy is expected to displace it.");
                continue;
            }

            chain.Middleware.Insert(0, MethodCall.For<ApiVersionHeaderWriter>(x => x.WriteAsync(null!)));
        }
    }
}
