using System.Security.Claims;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class user_name_relay_from_http : IntegrationContext
{
    public user_name_relay_from_http(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task user_name_is_set_from_claims_principal()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "testuser@example.com")],
            "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var result = await Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = principal);
            x.Get.Url("/user/name");
        });

        result.ReadAsText().ShouldBe("testuser@example.com");
    }

    [Fact]
    public async Task user_name_is_none_when_not_authenticated()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/user/name");
        });

        result.ReadAsText().ShouldBe("NONE");
    }
}
