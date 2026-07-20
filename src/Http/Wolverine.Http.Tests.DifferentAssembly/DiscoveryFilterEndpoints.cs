// Fixtures for http_endpoint_discovery_filter: two HTTP endpoints in two namespaces of one scanned
// assembly. Both follow the plain "*Endpoint" naming convention, so both are discovered by default;
// the test then excludes one namespace to show WolverineHttpOptions.CustomizeHttpEndpointDiscovery
// drops the endpoints under it.

namespace Wolverine.Http.Tests.DifferentAssembly.DiscoveryFilter.Included
{
    public static class IncludedPingEndpoint
    {
        [WolverineGet("/discovery-filter/included")]
        public static string Get() => "included";
    }
}

namespace Wolverine.Http.Tests.DifferentAssembly.DiscoveryFilter.Excluded
{
    public static class ExcludedPongEndpoint
    {
        [WolverineGet("/discovery-filter/excluded")]
        public static string Get() => "excluded";
    }
}
