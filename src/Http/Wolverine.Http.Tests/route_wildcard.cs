using Shouldly;

namespace Wolverine.Http.Tests;

public class route_wildcard : IntegrationContext
{
    public route_wildcard(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task wildcard()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/wildcard/one/two/three");
        });

        body.ReadAsText().ShouldBe("one/two/three");
    }
}