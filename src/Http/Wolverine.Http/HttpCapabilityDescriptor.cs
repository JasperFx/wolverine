using JasperFx.Descriptors;
using Wolverine.Configuration.Capabilities;

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

        // Intentionally no "Routes" child set (GH-3009): it summarized every route
        // (~96 KB on a Topicus-scale graph) and was fully redundant with
        // HttpGraphs[*].Chains, which the SPA already reads. The Sets collection on
        // this capability has no other consumer.

        return description;
    }
}
