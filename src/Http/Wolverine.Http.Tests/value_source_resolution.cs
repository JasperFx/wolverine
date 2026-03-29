using System.Security.Claims;
using Alba;
using Shouldly;

namespace Wolverine.Http.Tests;

public class value_source_resolution : IntegrationContext
{
    public value_source_resolution(AppFixture fixture) : base(fixture)
    {
    }

    private static ClaimsPrincipal UserWithClaims(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    #region Header tests

    [Fact]
    public async Task from_header_resolves_string_value()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-header/string");
            x.WithRequestHeader("X-Custom-Value", "hello-world");
        });

        result.ReadAsText().ShouldBe("hello-world");
    }

    [Fact]
    public async Task from_header_missing_string_returns_default()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-header/string");
        });

        result.ReadAsText().ShouldBe("no-value");
    }

    [Fact]
    public async Task from_header_resolves_int_value()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-header/int");
            x.WithRequestHeader("X-Count", "42");
        });

        result.ReadAsText().ShouldBe("count:42");
    }

    [Fact]
    public async Task from_header_resolves_guid_value()
    {
        var id = Guid.NewGuid();
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-header/guid");
            x.WithRequestHeader("X-Correlation-Id", id.ToString());
        });

        result.ReadAsText().ShouldBe($"id:{id}");
    }

    [Fact]
    public async Task from_header_int_missing_returns_default()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-header/int");
        });

        result.ReadAsText().ShouldBe("count:0");
    }

    #endregion

    #region Claim tests

    [Fact]
    public async Task from_claim_resolves_string_value()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-claim/string");
            x.ConfigureHttpContext(c => c.User = UserWithClaims(new Claim("sub", "user-123")));
        });

        result.ReadAsText().ShouldBe("user-123");
    }

    [Fact]
    public async Task from_claim_missing_string_returns_default()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-claim/string");
        });

        result.ReadAsText().ShouldBe("no-user");
    }

    [Fact]
    public async Task from_claim_resolves_int_value()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-claim/int");
            x.ConfigureHttpContext(c => c.User = UserWithClaims(new Claim("tenant-id", "42")));
        });

        result.ReadAsText().ShouldBe("tenant:42");
    }

    [Fact]
    public async Task from_claim_resolves_guid_value()
    {
        var id = Guid.NewGuid();
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-claim/guid");
            x.ConfigureHttpContext(c => c.User = UserWithClaims(new Claim("organization-id", id.ToString())));
        });

        result.ReadAsText().ShouldBe($"org:{id}");
    }

    [Fact]
    public async Task from_claim_int_missing_returns_default()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-claim/int");
        });

        result.ReadAsText().ShouldBe("tenant:0");
    }

    #endregion

    #region Method tests

    [Fact]
    public async Task from_method_resolves_guid_value()
    {
        var id = Guid.NewGuid();
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-method/guid");
            x.ConfigureHttpContext(c => c.User = UserWithClaims(new Claim("computed-id", id.ToString())));
        });

        result.ReadAsText().ShouldBe($"resolved:{id}");
    }

    [Fact]
    public async Task from_method_resolves_string_value()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-method/string");
            x.ConfigureHttpContext(c => c.User = UserWithClaims(new Claim("display-name", "Jeremy")));
        });

        result.ReadAsText().ShouldBe("name:Jeremy");
    }

    [Fact]
    public async Task from_method_with_no_claim_returns_default()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-method/string");
        });

        result.ReadAsText().ShouldBe("name:anonymous");
    }

    #endregion
}
