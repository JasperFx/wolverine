using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

// See NowConventionEndpoints. Message handlers have always supplied DateTimeOffset.UtcNow for a parameter
// named `now` through JasperFx's NowTimeVariableSource; these prove the same convention now reaches HTTP
// endpoints, and -- just as importantly -- that it did not eat ordinary date query string parameters on
// the way in.
public class now_parameter_convention : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        // Marten is here only because assembly wide endpoint discovery also picks up the [WriteAggregate] /
        // [Entity] endpoints that live alongside these, and those need a document store to codegen against.
        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "now_convention";
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Host.UseWolverine(opts => opts.Discovery.IncludeAssembly(GetType().Assembly));
        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.UseDeveloperExceptionPage();
            app.MapWolverineEndpoints();
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }
    }

    [Fact]
    public async Task datetimeoffset_now_is_supplied_as_utc_now()
    {
        var before = DateTimeOffset.UtcNow;

        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/now/offset");
            x.StatusCodeShouldBeOk();
        });

        var now = DateTimeOffset.Parse(await result.ReadAsTextAsync());

        now.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-5));
        now.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(5));
        now.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task datetime_now_is_supplied_too()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/now/datetime");
            x.StatusCodeShouldBeOk();
        });

        // RoundtripKind so the trailing Z is preserved as Kind.Utc rather than being converted to local time,
        // which would make the comparison against DateTime.UtcNow below wrong by the machine's offset.
        var now = DateTime.Parse(await result.ReadAsTextAsync(), null,
            System.Globalization.DateTimeStyles.RoundtripKind);

        now.Kind.ShouldBe(DateTimeKind.Utc);
        now.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(-5));
        now.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task the_name_match_is_case_insensitive()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/now/capitalized");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsTextAsync()).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task an_ordinary_date_query_parameter_still_binds_from_the_query_string()
    {
        // The regression this convention could easily have caused. IVariableSource matches on TYPE alone,
        // so applying it without the name gate would have handed this endpoint UtcNow and silently ignored
        // what the caller actually asked for.
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/now/range?from=2020-01-01T00:00:00Z&to=2020-12-31T00:00:00Z");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsTextAsync()).ShouldBe("2020-01-01 to 2020-12-31");
    }

    [Fact]
    public async Task an_explicit_now_route_argument_still_wins()
    {
        // RouteParameterStrategy runs before this one on purpose -- a route argument the author spelled out
        // is not a request for the clock.
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/now/route/2020-06-01T00:00:00Z");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsTextAsync()).ShouldBe("2020-06-01");
    }
}

public class NowConventionEndpoints
{
    [WolverineGet("/now/offset")]
    public static string GetOffset(DateTimeOffset now) => now.ToString("O");

    [WolverineGet("/now/datetime")]
    public static string GetDateTime(DateTime now) => now.ToString("O");

    [WolverineGet("/now/capitalized")]
    public static string GetCapitalized(DateTimeOffset Now) => Now.ToString("O");

    [WolverineGet("/now/range")]
    public static string GetRange(DateTimeOffset from, DateTimeOffset to)
        => $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}";

    [WolverineGet("/now/route/{now}")]
    public static string GetFromRoute(DateTimeOffset now) => now.ToString("yyyy-MM-dd");
}
