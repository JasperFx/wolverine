using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class endpoint_discovery_and_construction : IntegrationContext
{
    public endpoint_discovery_and_construction(AppFixture fixture) : base(fixture)
    {
    }

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

    [Fact]
    public void ability_to_discern_cascaded_messages_in_tuple_return_values()
    {
        var chain = HttpChains.ChainFor("POST", "/spawn");

        chain.InputType().ShouldBe(typeof(SpawnInput));
        chain.ResourceType.ShouldBe(typeof(string));
    }

    [Fact]
    public async Task find_endpoint_classes_that_have_the_attribute_but_not_the_naming_convention()
    {
        // The point here is to find endpoint methods where it uses the attribute, but not the endpoint
        // nomenclature
        var response = await Scenario(x => x.Get.Url("/discovered"));
        response.ReadAsText().ShouldBe("You found me!");
    }
}