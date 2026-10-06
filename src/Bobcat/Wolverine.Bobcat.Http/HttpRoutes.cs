using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Http;

namespace Wolverine.Bobcat.Http;

/// <summary>
/// Finds the Wolverine.HTTP route an act should call from the request it sends (GH-4834).
/// </summary>
public static class HttpRoutes
{
    /// <summary>
    /// The route of the one Wolverine.HTTP endpoint answering <paramref name="verb" /> whose request type
    /// is <paramref name="request" />'s type, with any route parameters filled from the request's
    /// properties of the same name. Refuses — naming the candidates — when there is none, or more
    /// than one, rather than guessing.
    /// </summary>
    public static string For(IHost host, object request, string verb = "POST")
    {
        var requestType = request.GetType();

        var chains = host.Services.GetServices<EndpointDataSource>()
            .SelectMany(x => x.Endpoints)
            .Select(x => x.Metadata.GetMetadata<HttpChain>())
            .Where(x => x is not null && x.RequestType == requestType && x.HttpMethods.Contains(verb, StringComparer.OrdinalIgnoreCase))
            .Select(x => x!)
            .DistinctBy(x => x.RoutePattern?.RawText)
            .ToArray();

        if (chains.Length == 0)
        {
            throw new InvalidOperationException(
                $"No Wolverine.HTTP endpoint accepts a {requestType.Name} on {verb}. Pass the route explicitly, or check the endpoint is mapped (MapWolverineEndpoints).");
        }

        if (chains.Length > 1)
        {
            throw new InvalidOperationException(
                $"{chains.Length} Wolverine.HTTP endpoints accept a {requestType.Name} on {verb} " +
                $"({string.Join(", ", chains.Select(x => x.RoutePattern?.RawText))}), so the route is ambiguous. Pass the route explicitly.");
        }

        return fill(chains[0], request);
    }

    private static string fill(HttpChain chain, object request)
    {
        var pattern = chain.RoutePattern ?? throw new InvalidOperationException($"The endpoint for {request.GetType().Name} has no route");
        var route = "/" + pattern.RawText!.TrimStart('/');

        foreach (var parameter in pattern.Parameters)
        {
            var property = request.GetType().GetProperty(parameter.Name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (property?.GetValue(request) is not { } value)
            {
                throw new InvalidOperationException(
                    $"The route {pattern.RawText} needs '{parameter.Name}', and {request.GetType().Name} has no non-null property of that name to fill it from. Pass the route explicitly.");
            }

            var segment = System.Text.RegularExpressions.Regex.Escape(parameter.Name);
            route = System.Text.RegularExpressions.Regex.Replace(route, $@"\{{\*?{segment}(:[^}}]*)?\??\}}",
                Uri.EscapeDataString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return route;
    }
}
