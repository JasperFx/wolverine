using System.Text.Json;
using Alba;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class content_negotiation_by_content_type : IntegrationContext
{
    public content_negotiation_by_content_type(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task route_to_v1_endpoint_by_content_type()
    {
        var body = JsonSerializer.Serialize(new CreateContentItemV1("Test Item"));

        var result = await Scenario(x =>
        {
            x.Post
                .Text(body)
                .ToUrl("/content-negotiation/items");
            x.WithRequestHeader("Content-Type", "application/vnd.item.v1+json");
            x.StatusCodeShouldBe(200);
        });

        var response = result.ReadAsJson<ContentItemCreated>();
        response.ShouldNotBeNull();
        response.Name.ShouldBe("Test Item");
        response.Version.ShouldBe("v1");
    }

    [Fact]
    public async Task route_to_v2_endpoint_by_content_type()
    {
        var body = JsonSerializer.Serialize(new CreateContentItemV2("Test Item", "Widgets"));

        var result = await Scenario(x =>
        {
            x.Post
                .Text(body)
                .ToUrl("/content-negotiation/items");
            x.WithRequestHeader("Content-Type", "application/vnd.item.v2+json");
            x.StatusCodeShouldBe(200);
        });

        var response = result.ReadAsJson<ContentItemCreated>();
        response.ShouldNotBeNull();
        response.Name.ShouldBe("Test Item");
        response.Category.ShouldBe("Widgets");
        response.Version.ShouldBe("v2");
    }

    [Fact]
    public async Task content_type_matching_is_case_insensitive()
    {
        var body = JsonSerializer.Serialize(new CreateContentItemV1("Test Item"));

        var result = await Scenario(x =>
        {
            x.Post
                .Text(body)
                .ToUrl("/content-negotiation/items");
            x.WithRequestHeader("Content-Type", "Application/Vnd.Item.V1+json");
            x.StatusCodeShouldBe(200);
        });

        var response = result.ReadAsJson<ContentItemCreated>();
        response.ShouldNotBeNull();
        response.Version.ShouldBe("v1");
    }
}
