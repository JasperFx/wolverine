using Asp.Versioning;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

public class WolverineApiVersioningOptionsTests
{
    [Fact]
    public void default_url_segment_prefix_is_v_token()
    {
        new WolverineApiVersioningOptions().UrlSegmentPrefix.ShouldBe("v{version}");
    }

    [Fact]
    public void default_url_formatter_emits_major_only()
    {
        var formatter = new WolverineApiVersioningOptions().UrlSegmentVersionFormatter;

        formatter(new ApiVersion(1, 0)).ShouldBe("1");
        formatter(new ApiVersion(2, 5)).ShouldBe("2");
    }

    [Fact]
    public void default_unversioned_policy_is_passthrough()
    {
        new WolverineApiVersioningOptions().UnversionedPolicy.ShouldBe(UnversionedPolicy.PassThrough);
    }

    [Fact]
    public void sunset_builder_stores_date_and_links()
    {
        var opts = new WolverineApiVersioningOptions();
        var date = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var linkUri = new Uri("https://example.com/sunset");

        opts.Sunset("1.0")
            .On(date)
            .WithLink(linkUri, "info", "text/html");

        var key = new ApiVersion(1, 0);
        opts.SunsetPolicies.ContainsKey(key).ShouldBeTrue();

        var policy = opts.SunsetPolicies[key];
        policy.Date.ShouldBe(date);
        policy.Links.Count.ShouldBe(1);
        policy.Links[0].LinkTarget.ShouldBe(linkUri);
        policy.Links[0].Title.ShouldBe("info");
        policy.Links[0].Type.ShouldBe("text/html");
    }

    [Fact]
    public void sunset_builder_supports_chaining_with_multiple_links()
    {
        var opts = new WolverineApiVersioningOptions();
        var date = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var uri1 = new Uri("https://example.com/sunset/1");
        var uri2 = new Uri("https://example.com/sunset/2");

        opts.Sunset("1.0")
            .On(date)
            .WithLink(uri1)
            .WithLink(uri2);

        var policy = opts.SunsetPolicies[new ApiVersion(1, 0)];
        policy.Links.Count.ShouldBe(2);
    }

    [Fact]
    public void deprecate_builder_stores_date()
    {
        var opts = new WolverineApiVersioningOptions();
        var date = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

        opts.Deprecate(new ApiVersion(2, 0)).On(date);

        var key = new ApiVersion(2, 0);
        opts.DeprecationPolicies.ContainsKey(key).ShouldBeTrue();
        opts.DeprecationPolicies[key].Date.ShouldBe(date);
    }

    [Fact]
    public void parse_string_overload_yields_same_dictionary_key()
    {
        var opts = new WolverineApiVersioningOptions();
        var date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        opts.Sunset("1.0").On(date);

        opts.SunsetPolicies.ContainsKey(new ApiVersion(1, 0)).ShouldBeTrue();
    }

    [Fact]
    public void sunset_builder_stores_date_only_with_no_links()
    {
        var opts = new WolverineApiVersioningOptions();
        var date = DateTimeOffset.UtcNow.AddDays(30);
        opts.Sunset("1.0").On(date);

        var policy = opts.SunsetPolicies[new ApiVersion(1, 0)];
        policy.Date.ShouldBe(date);
        policy.Links.Count.ShouldBe(0);
    }
}
