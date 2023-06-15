using Shouldly;

namespace Wolverine.Http.Tests;

public class using_custom_parameter_strategy : IntegrationContext
{
    public using_custom_parameter_strategy(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task it_works()
    {
        var body = await Scenario(x => x.Get.Url("/now"));

        var text = body.ReadAsText();
        
        DateTimeOffset.TryParse(text, out var _).ShouldBeTrue();
    }
}