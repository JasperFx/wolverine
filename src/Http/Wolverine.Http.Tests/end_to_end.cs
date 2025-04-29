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
        var body = await Scenario(x =>
        {
            x.Get.Url("/hello");
            x.StatusCodeShouldBeOk();

            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        (await body.ReadAsTextAsync()).ShouldBe("Hello");
    }
    
    [Fact]
    public async Task post_returning_fsharp_taskunit()
    {
        var body = await Scenario(x =>
        {
            x.Post.Url("/discovered-fsharp-unit");
            x.StatusCodeShouldBeOk();

            //x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        (await body.ReadAsTextAsync()).ShouldBe(null);
    }

    [Fact]
    public async Task retrieve_json_data()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/results/static");
            x.StatusCodeShouldBeOk();

            x.Header("content-type").SingleValueShouldEqual("application/json; charset=utf-8");
        });

        var results = body.ReadAsJson<ArithmeticResults>();
        results.Product.ShouldBe(4);
        results.Sum.ShouldBe(3);
    }

    [Fact]
    public async Task use_string_route_argument()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/name/Lebron");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Name is Lebron");
    }

    [Fact]
    public async Task use_enum_route_argument()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/enum/west");
        });

        body.ReadAsText().ShouldBe("Direction is West");
    }

    [Fact]
    public async Task use_int_route_argument_happy_path()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/age/49");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is 49");
    }

    [Fact]
    public async Task use_int_route_argument_sad_path()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/age/junk");
            x.StatusCodeShouldBe(404);
        });
    }
}