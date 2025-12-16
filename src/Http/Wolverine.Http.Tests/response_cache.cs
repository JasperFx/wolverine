using JasperFx.Core.Reflection;
using Shouldly;

namespace Wolverine.Http.Tests;

public class response_cache : IntegrationContext
{
    public response_cache(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task write_no_header_with_no_attribute()
    {
        var result = await Scenario(x => x.Get.Url("/cache/none"));
        result.Context.Response.Headers.ContainsKey("vary").ShouldBeFalse();
        result.Context.Response.Headers.ContainsKey("cache-control").ShouldBeFalse();
    }

    [Fact]
    public async Task write_cache_control_and_vary_by()
    {
        var result = await Scenario(x => x.Get.Url("/cache/one"));
        
        result.Context.Response.Headers["vary"].Single().ShouldBe("accept-encoding");
        result.Context.Response.Headers["cache-control"].Single().ShouldBe("max-age=3");
    }

    [Fact]
    public async Task write_cache_control_no_vary()
    {
        var result = await Scenario(x => x.Get.Url("/cache/two"));
        
        result.Context.Response.Headers["vary"].Any().ShouldBeFalse();
        result.Context.Response.Headers["cache-control"].Single().ShouldBe("no-store, max-age=10");
    }
}