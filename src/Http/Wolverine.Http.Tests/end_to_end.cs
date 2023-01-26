using Alba;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Results = WolverineWebApi.Results;

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
        
        (await body.ReadAsTextAsync()).ShouldBe("Hello");
    }

    [Fact]
    public async Task retrieve_json_data()
    {
        var body = await Host.Scenario(x =>
        {
            x.Get.Url("/results/static");
            x.StatusCodeShouldBeOk();

            x.Header("content-type").SingleValueShouldEqual("application/json; charset=utf-8");
        });

        var results = body.ReadAsJson<Results>();
        results.Product.ShouldBe(4);
        results.Sum.ShouldBe(3);
    }

    [Fact]
    public async Task use_string_route_argument()
    {
        var body = await Host.Scenario(x =>
        {
            x.Get.Url("/name/Lebron");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });
        
        body.ReadAsText().ShouldBe("Name is Lebron");
        // 0HMNVRSNL532U
    }
}