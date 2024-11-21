using Shouldly;
using WolverineWebApi.Validation;

namespace Wolverine.Http.Tests;

public class using_IResult_in_endpoints : IntegrationContext
{
    public using_IResult_in_endpoints(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task use_as_return_value_sync()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/result");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        result.ReadAsText().ShouldBe("Hello from result");
    }

    [Fact]
    public async Task use_as_return_value_async()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/result-async");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        result.ReadAsText().ShouldBe("Hello from async result");
    }

    [Fact]
    public async Task using_optional_result_in_middleware_when_result_is_null()
    {
        var result = await Scenario(x =>
        {
            x.Delete.Json(new BlockUser2("one")).ToUrl("/optional/result");
            x.Header("content-type").SingleValueShouldEqual("text/plain");
        });
        
        result.ReadAsText().ShouldBe("Ok - user blocked");
    }
    
    [Fact]
    public async Task using_optional_result_in_middleware_when_result_is_not_null()
    {
        var result = await Scenario(x =>
        {
            x.Delete.Json(new BlockUser2(null)).ToUrl("/optional/result");
            x.StatusCodeShouldBe(404);
        });
    }
}