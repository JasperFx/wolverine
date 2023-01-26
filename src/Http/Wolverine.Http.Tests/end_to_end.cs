using Alba;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class end_to_end : IntegrationContext
{
    public end_to_end(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task retrieve_text_data()
    {
        var body = await Host.Scenario(x =>
        {
            x.Get.Url("/hello");
            x.StatusCodeShouldBeOk();

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });
        
        body.ReadAsText().ShouldBe("Hello");
    }

    [Fact]
    public async Task retrieve_json_data()
    {
        var body = await Host.Scenario(x =>
        {
            x.Get.Url("/results/static");
            x.StatusCodeShouldBeOk();

            //x.Header("content-type").SingleValueShouldEqual("application/json; charset=utf-8");
        });

        body.Context.Response.Headers["content-type"].Single().ShouldBe("application/json; charset=utf-8");
        
        var results = body.ReadAsJson<Results>();
        results.Product.ShouldBe(4);
        results.Sum.ShouldBe(3);
    }
}