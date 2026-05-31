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

        var text = await result.ReadAsTextAsync();
        text.ShouldBe("hello-world");
    }

    [Fact]
    public async Task from_header_missing_string_returns_default()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-header/string");
        });

        var text = await result.ReadAsTextAsync();
        text.ShouldBe("no-value");
    }

    [Fact]
    public async Task from_header_resolves_int_value()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-header/int");
            x.WithRequestHeader("X-Count", "42");
        });

        var text = await result.ReadAsTextAsync();
        text.ShouldBe("count:42");
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

        var text = await result.ReadAsTextAsync();
        text.ShouldBe($"id:{id}");
    }

    [Fact]
    public async Task from_header_int_missing_returns_default()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-header/int");
        });

        var text = await result.ReadAsTextAsync();
        text.ShouldBe("count:0");
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

        var text = await result.ReadAsTextAsync();
        text.ShouldBe("user-123");
    }

    [Fact]
    public async Task from_claim_missing_string_returns_default()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-claim/string");
        });

        var text = await result.ReadAsTextAsync();
        text.ShouldBe("no-user");
    }

    [Fact]
    public async Task from_claim_resolves_int_value()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-claim/int");
            x.ConfigureHttpContext(c => c.User = UserWithClaims(new Claim("tenant-id", "42")));
        });

        var text = await result.ReadAsTextAsync();
        text.ShouldBe("tenant:42");
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

        var text = await result.ReadAsTextAsync();
        text.ShouldBe($"org:{id}");
    }

    [Fact]
    public async Task from_claim_int_missing_returns_default()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-claim/int");
        });

        var text = await result.ReadAsTextAsync();
        text.ShouldBe("tenant:0");
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

        var text = await result.ReadAsTextAsync();
        text.ShouldBe($"resolved:{id}");
    }

    [Fact]
    public async Task from_method_resolves_string_value()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-method/string");
            x.ConfigureHttpContext(c => c.User = UserWithClaims(new Claim("display-name", "Jeremy")));
        });

        var text = await result.ReadAsTextAsync();
        text.ShouldBe("name:Jeremy");
    }

    [Fact]
    public async Task from_method_with_no_claim_returns_default()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/test/from-method/string");
        });

        var text = await result.ReadAsTextAsync();
        text.ShouldBe("name:anonymous");
    }

    #endregion
}
