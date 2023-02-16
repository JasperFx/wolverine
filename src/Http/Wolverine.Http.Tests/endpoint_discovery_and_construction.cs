using JasperFx.Core.Reflection;
using Shouldly;

namespace Wolverine.Http.Tests;

public class endpoint_discovery_and_construction : IntegrationContext
{
    [Fact]
    public void discover_and_built_endpoints()
    {
        HttpChains.Endpoints.Any().ShouldBeTrue();
    }

    [Fact]
    public void read_order_from_attribute()
    {
        var chain = HttpChains.ChainFor("GET", "/fake/hello");
        chain.Endpoint.Order.ShouldBe(55);
    }

    [Fact]
    public void read_display_name_from_http_method_attribute()
    {
        var chain = HttpChains.ChainFor("GET", "/fake/hello");
        chain.Endpoint.DisplayName.ShouldBe("The Hello Route!");
    }

    public endpoint_discovery_and_construction(AppFixture fixture) : base(fixture)
    {
    }
}