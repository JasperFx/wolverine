using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Wolverine.ServiceDefaults;

namespace Wolverine.HealthChecks.Tests;

public class CritterStackServiceDefaultsTests
{
    private static WebApplicationBuilder newBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        return builder;
    }

    private static async Task<WebApplication> startAsync(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        app.MapCritterStackDefaultEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [Fact]
    public async Task health_and_alive_are_healthy_for_a_wolverine_service()
    {
        var builder = newBuilder();
        builder.UseWolverineFromAspire(opts => opts.ServiceName = "servicedefaults");

        await using var app = await startAsync(builder);
        var client = app.GetTestClient();

        var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");

        var alive = await client.GetAsync("/alive", TestContext.Current.CancellationToken);
        alive.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task registers_the_wolverine_checks_as_readiness_and_self_as_liveness()
    {
        var builder = newBuilder();
        builder.UseWolverineFromAspire();

        await using var app = await startAsync(builder);
        var registrations = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.ToDictionary(x => x.Name);

        registrations["self"].Tags.ShouldBe([CritterStackServiceDefaultsExtensions.LiveTag]);
        registrations["wolverine"].Tags.ShouldBe([CritterStackServiceDefaultsExtensions.ReadyTag]);
        registrations["wolverine-listeners"].Tags.ShouldBe([CritterStackServiceDefaultsExtensions.ReadyTag]);
        registrations["wolverine-transports"].Tags.ShouldBe([CritterStackServiceDefaultsExtensions.ReadyTag]);
    }

    [Fact]
    public async Task alive_ignores_a_failing_readiness_check_that_health_reports()
    {
        var builder = newBuilder();
        builder.UseWolverineFromAspire();
        builder.Services.AddHealthChecks()
            .AddCheck("not-ready", () => HealthCheckResult.Unhealthy("still warming up"),
                [CritterStackServiceDefaultsExtensions.ReadyTag]);

        await using var app = await startAsync(builder);
        var client = app.GetTestClient();

        (await client.GetAsync("/health", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync("/alive", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task the_defaults_are_healthy_in_an_application_without_wolverine()
    {
        var builder = newBuilder();
        builder.AddCritterStackServiceDefaults();

        await using var app = await startAsync(builder);

        (await app.GetTestClient().GetAsync("/health", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Wolverine")]
    [InlineData("Marten")]
    [InlineData("Polecat")]
    [InlineData("Fisher")]
    [InlineData("CritterWatch.Console")]
    public async Task traces_every_critter_stack_activity_source(string sourceName)
    {
        var exported = new List<Activity>();
        var builder = newBuilder();
        builder.AddCritterStackServiceDefaults();
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(exported));

        await using var app = await startAsync(builder);

        // A unique operation name, because ActivitySource listeners are process-wide and tests run in parallel
        var operation = $"servicedefaults-{Guid.NewGuid():N}";
        using (var source = new ActivitySource(sourceName))
        using (var activity = source.StartActivity(operation))
        {
            activity.ShouldNotBeNull();
        }

        app.Services.GetRequiredService<TracerProvider>().ForceFlush();
        exported.ShouldContain(x => x.DisplayName == operation && x.Source.Name == sourceName);
    }

    [Theory]
    [InlineData("Wolverine:servicedefaults")]
    [InlineData("Marten")]
    [InlineData("Polecat")]
    [InlineData("Fisher")]
    [InlineData("CritterWatch.Console")]
    [InlineData("Wolverine.CritterWatch")]
    public async Task collects_every_critter_stack_meter(string meterName)
    {
        var exported = new List<Metric>();
        var builder = newBuilder();
        builder.AddCritterStackServiceDefaults();
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter(exported));

        await using var app = await startAsync(builder);

        var instrument = $"servicedefaults.{Guid.NewGuid():N}";
        using var meter = new Meter(meterName);
        meter.CreateCounter<long>(instrument).Add(1);

        app.Services.GetRequiredService<MeterProvider>().ForceFlush();
        exported.ShouldContain(x => x.Name == instrument && x.MeterName == meterName);
    }

    [Fact]
    public void the_patterns_name_the_whole_stack()
    {
        foreach (var product in new[] { "Wolverine", "Marten", "Polecat", "Fisher", "CritterWatch" })
        {
            CritterStackTelemetry.ActivitySources.ShouldContain($"{product}*");
            CritterStackTelemetry.Meters.ShouldContain($"{product}*");
        }
    }
}
