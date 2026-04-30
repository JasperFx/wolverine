using Asp.Versioning;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

public class WolverineApiVersioningOpenApiOptionsTests
{
    [Fact]
    public void default_strategy_emits_v_major_for_major_minor()
    {
        var opts = new WolverineApiVersioningOpenApiOptions();

        opts.DocumentNameStrategy(new ApiVersion(1, 0)).ShouldBe("v1");
        opts.DocumentNameStrategy(new ApiVersion(2, 5)).ShouldBe("v2");
    }

    [Fact]
    public void default_strategy_falls_back_for_date_versions()
    {
        var opts = new WolverineApiVersioningOpenApiOptions();
        var dateVersion = new ApiVersion(new DateTime(2024, 11, 1));

        var result = opts.DocumentNameStrategy(dateVersion);

        result.ShouldBe(dateVersion.ToString());
    }

    [Fact]
    public void custom_strategy_overrides_default()
    {
        var opts = new WolverineApiVersioningOpenApiOptions();
        opts.DocumentNameStrategy = v => $"v{v.MajorVersion}.{v.MinorVersion}";

        opts.DocumentNameStrategy(new ApiVersion(1, 0)).ShouldBe("v1.0");
        opts.DocumentNameStrategy(new ApiVersion(2, 3)).ShouldBe("v2.3");
    }
}
