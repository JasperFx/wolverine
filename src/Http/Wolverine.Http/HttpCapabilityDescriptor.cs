using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Wolverine.Configuration.Capabilities;
using Wolverine.Http.CodeGen;

namespace Wolverine.Http;

/// <summary>
/// Describes Wolverine.HTTP capabilities including options and all HTTP routes
/// for display in CritterWatch.
/// </summary>
public class HttpCapabilityDescriptor : ICapabilityDescriptor
{
    private readonly WolverineHttpOptions _options;

    public HttpCapabilityDescriptor(WolverineHttpOptions options)
    {
        _options = options;
    }

    public OptionsDescription Describe()
    {
        var description = new OptionsDescription
        {
            Subject = "Wolverine.Http"
        };

        description.AddTag("http");

        description.AddValue(nameof(_options.WarmUpRoutes), _options.WarmUpRoutes);
        description.AddValue(nameof(_options.ServiceProviderSource), _options.ServiceProviderSource);

        var endpoints = _options.Endpoints;
        if (endpoints != null)
        {
            var routeSet = description.AddChildSet("Routes");
            routeSet.SummaryColumns = ["HttpMethods", "Route", "Endpoint"];

            foreach (var chain in endpoints.Chains.OrderBy(c => c.RoutePattern?.RawText ?? string.Empty))
            {
                var routeDescription = new OptionsDescription
                {
                    Subject = chain.RoutePattern?.RawText ?? string.Empty,
                    Title = chain.RoutePattern?.RawText ?? string.Empty
                };

                routeDescription.AddValue("HttpMethods", chain.HttpMethods.Join(", "));
                routeDescription.AddValue("Route", chain.RoutePattern?.RawText ?? string.Empty);
                routeDescription.AddValue("Endpoint",
                    $"{chain.Method.HandlerType.FullNameInCode()}.{chain.Method.Method.Name}");

                routeSet.Rows.Add(routeDescription);
            }
        }

        return description;
    }
}
