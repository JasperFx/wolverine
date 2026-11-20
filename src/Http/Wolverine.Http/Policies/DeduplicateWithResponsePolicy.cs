using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Runtime;

namespace Wolverine.Http.Policies;

/// <summary>
/// GH-5617. Applies <c>[DeduplicatedWithResponse]</c> to every endpoint <paramref name="filter" /> matches. See
/// <see cref="WolverineHttpOptions.DeduplicateWithResponse" />.
/// </summary>
internal class DeduplicateWithResponsePolicy(Func<HttpChain, bool> filter, DeduplicatedWithResponseRequirement defaults)
    : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            if (!filter(chain) || chain.OptsOutOfDeduplicatedWithResponse()) continue;

            // Attributes are applied when the chain is built, so this runs after them: keep everything the endpoint
            // set for itself, and fill in the rest
            if (chain.DeduplicatedWithResponse is { } own)
            {
                chain.DeduplicatedWithResponse = own.InheritFrom(defaults);
                continue;
            }

            // An endpoint the requirement would refuse, one returning no resource above all, is left alone instead
            // of failing startup
            if (!chain.CanDeduplicateWithResponse()) continue;

            chain.DeduplicatedWithResponse = defaults;
        }
    }

    internal static bool IsPost(HttpChain chain) => chain.HttpMethods.Contains("POST");
}
