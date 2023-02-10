using Shouldly;

namespace Wolverine.Http.Tests;

public class using_IResult_in_endpoints : IntegrationContext
{
    [Fact]
    public async Task use_as_return_value_sync()
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/result");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });
        
        result.ReadAsText().ShouldBe("Hello from result");
    }
    
    [Fact]
    public async Task use_as_return_value_async()
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/result/async");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });
        
        result.ReadAsText().ShouldBe("Hello from async result");
    }

    public using_IResult_in_endpoints(AppFixture fixture) : base(fixture)
    {
    }
}