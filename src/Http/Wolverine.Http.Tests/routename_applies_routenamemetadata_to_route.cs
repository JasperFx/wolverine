using Microsoft.AspNetCore.Routing;
using Shouldly;

namespace Wolverine.Http.Tests;

public class routename_applies_routenamemetadata_to_route : IntegrationContext
{
    public routename_applies_routenamemetadata_to_route(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void routename_applies_routenamemetadata()
    {
        var chain = HttpChains.ChainFor("POST", "/named/route");
        chain.Endpoint.Metadata.Any(m => m is RouteNameMetadata { RouteName: "NamedRoute"}).ShouldBeTrue();
    }
}