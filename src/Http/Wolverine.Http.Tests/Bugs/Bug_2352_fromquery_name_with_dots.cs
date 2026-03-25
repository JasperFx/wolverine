using Shouldly;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_2352_fromquery_name_with_dots : IntegrationContext
{
    public Bug_2352_fromquery_name_with_dots(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task should_use_dotted_fromquery_name_for_string_parameters()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/dotted?hub.mode=subscribe&hub.challenge=abc123&hub.verify_token=mytoken");
        });

        body.ReadAsText().ShouldBe("subscribe|abc123|mytoken");
    }

    [Fact]
    public async Task should_use_dotted_fromquery_name_for_int_parameter()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/dotted-int?page.number=42");
        });

        body.ReadAsText().ShouldBe("page 42");
    }
}
