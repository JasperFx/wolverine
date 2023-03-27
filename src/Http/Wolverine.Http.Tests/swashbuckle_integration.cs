using Shouldly;

namespace Wolverine.Http.Tests;

public class swashbuckle_integration : IntegrationContext
{
    public swashbuckle_integration(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task wolverine_stuff_is_in_the_document()
    {
        var results = await Scenario(x => { x.Get.Url("/swagger/v1/swagger.json"); });

        var doc = results.ReadAsText();

        doc.ShouldContain("/fromservice");
    }
}