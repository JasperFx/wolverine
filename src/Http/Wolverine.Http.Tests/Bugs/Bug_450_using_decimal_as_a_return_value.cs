using Shouldly;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_450_using_decimal_as_a_return_value : IntegrationContext
{
    public Bug_450_using_decimal_as_a_return_value(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task get_the_decimal_body()
    {
        var body = await Scenario(x => x.Get.Url("/api/myapp/registration-price?numberOfMembers=100"));
        var raw = body.ReadAsText();
        decimal.Parse(raw).ShouldBe(28000m);
    }
}