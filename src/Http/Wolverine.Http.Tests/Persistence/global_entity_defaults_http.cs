using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Refit;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Persistence;

namespace Wolverine.Http.Tests.Persistence;

public class global_entity_defaults_http : IAsyncLifetime
{
    private IAlbaHost theHost;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "global_defaults_http";
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(GetType().Assembly);

            // Set global default to ProblemDetailsWith404
            opts.EntityDefaults.OnMissing = OnMissing.ProblemDetailsWith404;
        });

        builder.Services.AddWolverineHttp();

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
    public async Task global_default_changes_http_response()
    {
        // With global OnMissing = ProblemDetailsWith404, a plain [Entity] endpoint should return
        // 404 with ProblemDetails body
        var tracked = await theHost.Scenario(x =>
        {
            x.Get.Url("/global-defaults/todo/nonexistent");
            x.StatusCodeShouldBe(404);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var details = tracked.ReadAsJson<ProblemDetails>();
        details.Detail.ShouldBe("Unknown Todo2 with identity nonexistent");
    }

    [Fact]
    public async Task attribute_override_wins_over_global()
    {
        // Explicit [Entity(OnMissing = Simple404)] should override the global ProblemDetailsWith404
        // and return a plain 404 with empty body
        var tracked = await theHost.Scenario(x =>
        {
            x.Get.Url("/global-defaults/todo-simple/nonexistent");
            x.StatusCodeShouldBe(404);
        });

        tracked.ReadAsText().ShouldBeEmpty();
    }
}
