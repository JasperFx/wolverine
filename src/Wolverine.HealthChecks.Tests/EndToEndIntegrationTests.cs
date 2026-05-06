using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Wolverine.HealthChecks;
using Wolverine.Runtime;

namespace Wolverine.HealthChecks.Tests;

public class EndToEndIntegrationTests
{
    private static IHostBuilder BuildHostBuilder()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddHealthChecks()
                        .AddWolverine()
                        .AddWolverineListeners();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                    {
                        // Default endpoint = all checks.
                        e.MapHealthChecks("/health");

                        // Tag-filtered endpoints — the canonical K8s liveness/readiness split.
                        e.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                        {
                            Predicate = check => check.Tags.Contains("ready")
                        });
                        e.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                        {
                            Predicate = check => check.Tags.Contains("live")
                        });
                    });
                });
            })
            .UseWolverine();
    }

    [Fact]
    public async Task health_endpoint_reports_healthy_after_startup()
    {
        using var host = await BuildHostBuilder().StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task tag_scoping_separates_liveness_and_readiness()
    {
        var host = await Host.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddHealthChecks()
                        .AddWolverine(tags: new[] { "live", "ready" })
                        .AddWolverineListeners(tags: new[] { "ready" });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                    {
                        e.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                        {
                            Predicate = check => check.Tags.Contains("ready")
                        });
                        e.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                        {
                            Predicate = check => check.Tags.Contains("live")
                        });
                    });
                });
            })
            .UseWolverine()
            .StartAsync();

        try
        {
            var client = host.GetTestClient();

            (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
