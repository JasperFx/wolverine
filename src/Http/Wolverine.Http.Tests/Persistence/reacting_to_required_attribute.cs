using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;
using Xunit.Abstractions;

public class reacting_to_required_attribute : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IAlbaHost theHost;

    public reacting_to_required_attribute(ITestOutputHelper output)
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
        if (theHost != null) await theHost.StopAsync();
    }

    [Fact]
    public async Task required_attribute_404_behavior_on_missing()
    {
        var tracked = await theHost.Scenario(x =>
        {
            x.Get.Url("/required/todo404required/nonexistent");
            x.StatusCodeShouldBe(404);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var details = tracked.ReadAsJson<ProblemDetails>();
        details.Detail.ShouldBe("Todo not found by id");
    }
}