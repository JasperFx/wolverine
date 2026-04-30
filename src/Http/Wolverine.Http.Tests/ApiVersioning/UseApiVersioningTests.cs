using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

public class UseApiVersioningTests
{
    [Fact]
    public void use_api_versioning_stores_options()
    {
        var opts = new WolverineHttpOptions();

        opts.UseApiVersioning(v => v.UrlSegmentPrefix = "api/v{version}");

        opts.ApiVersioning.ShouldNotBeNull();
        opts.ApiVersioning.UrlSegmentPrefix.ShouldBe("api/v{version}");
    }

    [Fact]
    public void use_api_versioning_called_twice_accumulates()
    {
        var opts = new WolverineHttpOptions();
        var date1 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var date2 = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        opts.UseApiVersioning(v => v.Sunset("1.0").On(date1));
        opts.UseApiVersioning(v => v.Deprecate("2.0").On(date2));

        opts.ApiVersioning.ShouldNotBeNull();

        var av = opts.ApiVersioning;
        av.SunsetPolicies.ContainsKey(new Asp.Versioning.ApiVersion(1, 0)).ShouldBeTrue();
        av.DeprecationPolicies.ContainsKey(new Asp.Versioning.ApiVersion(2, 0)).ShouldBeTrue();
    }

    [Fact]
    public void null_configure_throws_argument_null()
    {
        var opts = new WolverineHttpOptions();

        Should.Throw<ArgumentNullException>(() => opts.UseApiVersioning(null!));
    }
}
