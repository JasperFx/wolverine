using Alba;
using Shouldly;

namespace Wolverine.Http.Tests.Caching;

public class output_cache : IntegrationContext
{
    public output_cache(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task output_cached_endpoint_returns_same_response()
    {
        // First request
        var first = await Scenario(s =>
        {
            s.Get.Url("/api/cached");
            s.StatusCodeShouldBeOk();
        });
        var firstBody = first.ReadAsText();

        // Second request should return cached response
        var second = await Scenario(s =>
        {
            s.Get.Url("/api/cached");
            s.StatusCodeShouldBeOk();
        });
        var secondBody = second.ReadAsText();

        // Should be the same cached response
        secondBody.ShouldBe(firstBody);
    }

    [Fact]
    public async Task non_cached_endpoint_returns_different_response()
    {
        var first = await Scenario(s =>
        {
            s.Get.Url("/api/not-cached");
            s.StatusCodeShouldBeOk();
        });
        var firstBody = first.ReadAsText();

        var second = await Scenario(s =>
        {
            s.Get.Url("/api/not-cached");
            s.StatusCodeShouldBeOk();
        });
        var secondBody = second.ReadAsText();

        // Should be different responses
        secondBody.ShouldNotBe(firstBody);
    }

    [Fact]
    public async Task output_cached_default_endpoint_returns_same_response()
    {
        // First request
        var first = await Scenario(s =>
        {
            s.Get.Url("/api/cached-default");
            s.StatusCodeShouldBeOk();
        });
        var firstBody = first.ReadAsText();

        // Second request should return cached response
        var second = await Scenario(s =>
        {
            s.Get.Url("/api/cached-default");
            s.StatusCodeShouldBeOk();
        });
        var secondBody = second.ReadAsText();

        // Should be the same cached response
        secondBody.ShouldBe(firstBody);
    }
}
