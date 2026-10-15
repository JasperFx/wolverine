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

        var pattern = chains[0].RoutePattern ?? throw new InvalidOperationException($"The endpoint for {requestType.Name} has no route");
        return FillFrom(pattern.RawText!, request);
    }

    /// <summary>
    /// The route template of the one endpoint whose ASP.NET Core route name is <paramref name="routeName" />
    /// (<c>RouteName</c> on a Wolverine.HTTP attribute, <c>.WithName()</c> on a minimal API), answering
    /// <paramref name="verb" /> when one is given. Refuses when no endpoint, or more than one template, carries it.
    /// </summary>
    public static string Named(IHost host, string routeName, string? verb = null)
    {
        var templates = host.Services.GetServices<EndpointDataSource>()
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(x => x.Metadata.GetMetadata<IRouteNameMetadata>()?.RouteName == routeName)
            .Where(x => verb is null || x.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } methods ||
                        methods.HttpMethods.Count == 0 || methods.HttpMethods.Contains(verb, StringComparer.OrdinalIgnoreCase))
            .Select(x => "/" + x.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .ToArray();

        return templates.Length switch
        {
            1 => templates[0],
            0 => throw new InvalidOperationException(
                $"No endpoint{(verb is null ? "" : $" answering {verb}")} has the route name '{routeName}'. Check the RouteName on its attribute, or pass the route."),
            _ => throw new InvalidOperationException(
                $"{templates.Length} endpoints have the route name '{routeName}' ({string.Join(", ", templates)}), so it is ambiguous. Pass the route.")
        };
    }

    /// <summary>
    /// <paramref name="template" /> with its route parameters filled from <paramref name="values" />, in order:
    /// <c>Fill("/api/orders/{id}", id)</c>. Refuses a count that does not match the template's parameters.
    /// </summary>
    public static string Fill(string template, params object[] values)
    {
        var i = 0;
        var route = System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}]+\}",
            m => i < values.Length ? escape(values[i++]) : m.Value);

        var holes = System.Text.RegularExpressions.Regex.Matches(template, @"\{[^}]+\}").Count;
        if (holes != values.Length)
            throw new ArgumentException(
                $"The route '{template}' has {holes} parameter(s), but {values.Length} value(s) were given to fill it.", nameof(values));

        return route;
    }

    /// <summary><paramref name="template" /> with its route parameters filled from <paramref name="request" />'s properties of the same name.</summary>
    public static string FillFrom(string template, object request)
    {
        var pattern = Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(template);
        var route = "/" + template.TrimStart('/');

        foreach (var parameter in pattern.Parameters)
        {
            var property = request.GetType().GetProperty(parameter.Name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (property?.GetValue(request) is not { } value)
            {
                throw new InvalidOperationException(
                    $"The route {template} needs '{parameter.Name}', and {request.GetType().Name} has no non-null property of that name to fill it from. Pass the route explicitly.");
            }

            var segment = System.Text.RegularExpressions.Regex.Escape(parameter.Name);
            route = System.Text.RegularExpressions.Regex.Replace(route, $@"\{{\*?{segment}(:[^}}]*)?\??\}}",
                escape(value),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return route;
    }

    private static string escape(object value)
        => Uri.EscapeDataString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
}
