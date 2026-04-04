using Alba;
using Shouldly;
using WolverineWebApi;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Http.Tests;

public class ContentNegotiationTests : IntegrationContext
{
    private readonly ITestOutputHelper _output;

    public ContentNegotiationTests(AppFixture fixture, ITestOutputHelper output) : base(fixture)
    {
        _output = output;
    }

    [Fact]
    public async Task writes_text_plain_when_accept_header_matches()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/conneg/write");
            x.WithRequestHeader("Accept", "text/plain");
            x.StatusCodeShouldBeOk();
        });

        var text = result.ReadAsText();
        text.ShouldBe("Widget: 42");
    }

    [Fact]
    public async Task writes_csv_when_accept_header_matches()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/conneg/write");
            x.WithRequestHeader("Accept", "text/csv");
            x.StatusCodeShouldBeOk();
        });

        var text = result.ReadAsText();
        text.ShouldBe("Name,Value\nWidget,42");
    }

    [Fact]
    public async Task falls_back_to_json_in_loose_mode()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/conneg/write");
            x.WithRequestHeader("Accept", "application/json");
            x.StatusCodeShouldBeOk();
        });

        var item = result.ReadAsJson<ConnegItem>();
        item.ShouldNotBeNull();
        item!.Name.ShouldBe("Widget");
        item.Value.ShouldBe(42);
    }

    [Fact]
    public async Task falls_back_to_json_with_no_accept_header()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/conneg/loose");
            // No Accept header — falls back to JSON in loose mode
            x.StatusCodeShouldBeOk();
        });

        var item = result.ReadAsJson<ConnegItem>();
        item.ShouldNotBeNull();
        item!.Name.ShouldBe("LooseWidget");
    }

    [Fact]
    public async Task strict_mode_returns_406_when_no_match()
    {
        await Scenario(x =>
        {
            x.Get.Url("/conneg/strict");
            x.WithRequestHeader("Accept", "application/xml");
            x.StatusCodeShouldBe(406);
        });
    }

    [Fact]
    public async Task strict_mode_returns_ok_when_match()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/conneg/strict");
            x.WithRequestHeader("Accept", "text/plain");
            x.StatusCodeShouldBeOk();
        });

        var text = result.ReadAsText();
        text.ShouldBe("StrictWidget: 99");
    }

    [Fact]
    public async Task loose_mode_text_plain_works()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/conneg/loose");
            x.WithRequestHeader("Accept", "text/plain");
            x.StatusCodeShouldBeOk();
        });

        var text = result.ReadAsText();
        text.ShouldBe("LooseWidget: 77");
    }
}
