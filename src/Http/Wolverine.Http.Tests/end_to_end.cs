using System.Globalization;
using System.Net.Http.Headers;
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

    [Fact]
    public async Task use_parsed_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int?age=8");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is 8");
    }
    
    [Fact]
    public async Task use_parsed_enum_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/enum?direction=north");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("North");
    }
    
        
    [Fact]
    public async Task use_parsed_enum_querystring_hit_2()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/enum?direction=North");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("North");
    }

    [Fact]
    public async Task use_parsed_querystring_complete_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is ");
    }

    [Fact]
    public async Task use_parsed_querystring_bad_data()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int?age=garbage");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is ");
    }

    [Fact]
    public async Task use_parsed_nullable_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int/nullable?age=11");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is 11");
    }

    [Fact]
    public async Task use_parsed_nullable_querystring_complete_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int/nullable");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is missing");
    }

    [Fact]
    public async Task use_parsed_nullable_querystring_bad_data()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/int/nullable?age=garbage");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Age is missing");
    }

    #region sample_query_string_usage

    [Fact]
    public async Task use_string_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/string?name=Magic");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Name is Magic");
    }

    [Fact]
    public async Task use_string_querystring_miss()
    {
        var body = await Scenario(x =>
        {
            x.Get.Url("/querystring/string");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Name is missing");
    }

    [Fact]
    public async Task use_decimal_querystring_hit()
    {
        var body = await Scenario(x =>
        {
            x.WithRequestHeader("Accept-Language", "fr-FR");
            x.Get.Url("/querystring/decimal?amount=42.1");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        body.ReadAsText().ShouldBe("Amount is 42.1");
    }

    #endregion
}

