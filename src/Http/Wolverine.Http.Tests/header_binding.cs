﻿using Alba;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class header_binding : IntegrationContext
{
    public header_binding(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task read_single_header_with_explicit_name_mapping()
    {
        await Scenario(x =>
        {
            x.WithRequestHeader("x-wolverine", "one");
            x.Get.Url("/headers/simple");
            x.ContentShouldBe("one");
        });
    }

    [Fact]
    public async Task read_single_parsed_header_with_explicit_name_mapping()
    {
        await Scenario(x =>
        {
            x.WithRequestHeader("x-wolverine", "111");
            x.Get.Url("/headers/int");
            x.ContentShouldBe("222");
        });
    }

    [Fact]
    public async Task read_header_by_variable_name()
    {
        await Scenario(x =>
        {
            x.WithRequestHeader("accepts", "text/plain");

            x.Get.Url("/headers/accepts");
            x.ContentShouldBe("text/plain");
        });
    }

    [Fact]
    public async Task can_use_FromHeader_on_middleware()
    {
        await Scenario(x =>
        {
            x.WithRequestHeader("x-wolverine", "one");
            x.WithRequestHeader("x-day", "Friday");
            x.Get.Url("/headers/simple");
        });

        HeaderUsingEndpoint.Day.ShouldBe("Friday");
    }
    
    [Fact]
    public async Task from_header_in_middleware()
    {
        var result = await Scenario(s =>
        {
            s.Get.Url("/middleware/header");
            s.WithRequestHeader("x-handler", "handler");
            s.WithRequestHeader("x-before", "before");
            s.WithRequestHeader("x-middleware", "middleware");
            s.StatusCodeShouldBeOk();
        });

        var response = await result.ReadAsJsonAsync<MiddlewareEndpoint.Result>();
        
        response.Handler.ShouldBe("handler");
        response.Before.ShouldBe("before");
        response.Middleware.ShouldBe("middleware");
    }
}