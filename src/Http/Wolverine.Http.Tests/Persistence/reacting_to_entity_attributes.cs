using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Refit;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Persistence;
using Xunit.Abstractions;

namespace Wolverine.Http.Tests.Persistence;


// See RequiredTodoEndpoint
public class reacting_to_entity_attributes : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IAlbaHost theHost;

    public reacting_to_entity_attributes(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        // config
        builder.Services.AddMarten(opts =>
        {
            // Establish the connection string to your Marten database
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "onmissing";
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Host.UseWolverine(opts => opts.Discovery.IncludeAssembly(GetType().Assembly));

        builder.Services.AddWolverineHttp();

        // This is using Alba, which uses WebApplicationFactory under the covers
        theHost = await AlbaHost.For(builder, app =>
        {
            app.UseDeveloperExceptionPage();
            app.MapWolverineEndpoints();
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (theHost != null)
        {
            await theHost.StopAsync();
        }
    }

    [Fact]
    public async Task default_404_behavior_on_missing()
    {
        var tracked = await theHost.Scenario(x =>
        {
            x.Get.Url("/required/todo404/nonexistent");
            x.StatusCodeShouldBe(404);
        });
        
        tracked.ReadAsText().ShouldBeEmpty();
    }

    [Fact]
    public async Task problem_details_400_on_missing()
    {
        var tracked = await theHost.Scenario(x =>
        {
            x.Get.Url("/required/todo400/nonexistent");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var details = tracked.ReadAsJson<ProblemDetails>();
        details.Detail.ShouldBe("Unknown Todo2 with identity nonexistent");
    }
    
    [Fact]
    public async Task problem_details_404_on_missing()
    {
        var tracked = await theHost.Scenario(x =>
        {
            x.Get.Url("/required/todo3/nonexistent");
            x.StatusCodeShouldBe(404);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var details = tracked.ReadAsJson<ProblemDetails>();
        details.Detail.ShouldBe("Unknown Todo2 with identity nonexistent");
    }

    [Fact]
    public async Task throw_exception_on_missing()
    {
        var tracked = await theHost.Scenario(x =>
        {
            x.Get.Url("/required/todo4/nonexistent");
            x.StatusCodeShouldBe(500);
        });

        var text = tracked.ReadAsText();
        text.ShouldContain(typeof(RequiredDataMissingException).FullName);
    }
    
    [Fact]
    public async Task problem_details_400_on_missing_with_custom_message()
    {
        var tracked = await theHost.Scenario(x =>
        {
            x.Get.Url("/required/todo5/nonexistent");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var details = tracked.ReadAsJson<ProblemDetails>();
        details.Detail.ShouldBe("Wrong id man!");
    }

    [Fact]
    public async Task problem_details_400_on_missing_with_custom_message_using_id()
    {
        var tracked = await theHost.Scenario(x =>
        {
            x.Get.Url("/required/todo6/nonexistent");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var details = tracked.ReadAsJson<ProblemDetails>();
        details.Detail.ShouldBe("Id 'nonexistent' is wrong!");
    }
    
    [Fact]
    public async Task throw_exception_on_missing_with_custom_message()
    {
        var tracked = await theHost.Scenario(x =>
        {
            x.Get.Url("/required/todo7/nonexistent");
            x.StatusCodeShouldBe(500);
        });

        var text = tracked.ReadAsText();
        text.ShouldContain(typeof(RequiredDataMissingException).FullName);
        text.ShouldContain("Id 'nonexistent' is wrong!");
    }
    
}