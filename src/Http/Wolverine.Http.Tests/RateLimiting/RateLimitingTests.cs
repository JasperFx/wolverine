using Alba;
using Shouldly;

namespace Wolverine.Http.Tests.RateLimiting;

public class RateLimitingTests : IntegrationContext
{
    public RateLimitingTests(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task rate_limited_endpoint_returns_429_when_limit_exceeded()
    {
        // First request should succeed
        await Host.Scenario(s =>
        {
            s.Get.Url("/api/rate-limited");
            s.StatusCodeShouldBeOk();
        });

        // Second request within the window should be rate limited
        await Host.Scenario(s =>
        {
            s.Get.Url("/api/rate-limited");
            s.StatusCodeShouldBe(429);
        });
    }

    [Fact]
    public async Task non_rate_limited_endpoint_always_succeeds()
    {
        for (int i = 0; i < 5; i++)
        {
            await Host.Scenario(s =>
            {
                s.Get.Url("/api/not-rate-limited");
                s.StatusCodeShouldBeOk();
            });
        }
    }
}
