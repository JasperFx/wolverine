using Alba;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using WolverineWebApi;
using Results = WolverineWebApi.Results;

namespace Wolverine.Http.Tests;

public class end_to_end : IntegrationContext
{
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

        var results = body.ReadAsJson<Results>();
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


    public end_to_end(AppFixture fixture) : base(fixture)
    {
    }
}