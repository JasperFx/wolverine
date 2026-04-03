using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Wolverine.Http;

internal class RoutePrefixPolicy : IHttpPolicy
{
    private readonly WolverineHttpOptions _options;

    public RoutePrefixPolicy(WolverineHttpOptions options)
    {
        _options = options;
    }

    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        // Nothing to do if no prefixes are configured
        if (_options.GlobalRoutePrefix == null && _options.NamespacePrefixes.Count == 0)
        {
            return;
        }

        foreach (var chain in chains)
        {
            if (chain.RoutePattern == null) continue;

            var prefix = DeterminePrefix(chain);
            if (prefix == null) continue;

            PrependPrefix(chain, prefix);
        }
    }

    internal string? DeterminePrefix(HttpChain chain)
    {
        // 1. Check for [RoutePrefix] attribute on the handler type (most specific)
        if (chain.Method.HandlerType.TryGetAttribute<RoutePrefixAttribute>(out var attr))
        {
            return attr.Prefix;
        }

        // 2. Check for namespace-specific prefix
        var handlerNamespace = chain.Method.HandlerType.Namespace;
        if (handlerNamespace != null)
        {
            // Find the most specific (longest) matching namespace prefix
            var match = _options.NamespacePrefixes
                .Where(np => handlerNamespace == np.Namespace || handlerNamespace.StartsWith(np.Namespace + "."))
                .OrderByDescending(np => np.Namespace.Length)
                .FirstOrDefault();

            if (match.Prefix != null)
            {
                return match.Prefix;
            }
        }

        // 3. Fall back to global prefix
        return _options.GlobalRoutePrefix;
    }

    internal static void PrependPrefix(HttpChain chain, string prefix)
    {
        var currentRoute = chain.RoutePattern!.RawText ?? string.Empty;
        var trimmedRoute = currentRoute.TrimStart('/');
        var newRoute = $"/{prefix}/{trimmedRoute}".TrimEnd('/');

        // Re-parse the route pattern with the new prefix
        chain.RoutePattern = RoutePatternFactory.Parse(newRoute);
    }
}
